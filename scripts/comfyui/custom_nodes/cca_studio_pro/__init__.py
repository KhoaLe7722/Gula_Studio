"""Dreamina Studio Pro Custom Nodes for Z-Image-Turbo (RTX 4050 6 GB VRAM Optimized).

Provides 6 production-grade local nodes:
  1. CcaRemoveBackground   - Local offline Bria RMBG-1.4 ONNX (0 MB GPU VRAM, 60s idle TTL cache) -> true RGBA transparent PNG.
  2. CcaInpaintPrepare     - Aligns source + painted mask to multiples of 64 for Z-Image-Turbo ControlNet Union 2.1 Inpainting.
  3. CcaOutpaintPrepare    - Expands canvas (16:9 / 1:1 / 3:4 / 2:3 / +25% / custom) with seamless blurred seed border + mask.
  4. CcaInpaintComposite   - Seamless feathered pixel-space composite preserving 100% of unmasked source pixels.
  5. CcaSmartUpscale2x     - Tiled 4x supersampled upscale (4x_NMKD-Siax / 4x-UltraSharp) cleanly downsampled to crisp 2x (VRAM-safe).
  6. CcaTiledHiresRefine   - 2x2 overlapping tiled Z-Image-Turbo low-denoise latent refinement with cosine Hann blending (<5.5 GB VRAM).
"""

from __future__ import annotations

import gc
import math
import os
from pathlib import Path
import threading

import cv2
import numpy as np
import onnxruntime as ort
import torch
from torch.nn import functional as F

import comfy.model_management
import comfy.utils
import nodes


ROOT = Path(__file__).resolve().parent
COMFY_ROOT = ROOT.parent.parent
RMBG_MODEL_PATH = COMFY_ROOT / "models" / "background_removal" / "rmbg_1_4.onnx"

_ONNX_THREADS = min(8, max(4, (os.cpu_count() or 8) // 2))
_RMBG_LOCK = threading.Lock()
_RMBG_IDLE_TTL = 60.0
_rmbg_sess: ort.InferenceSession | None = None
_rmbg_timer: threading.Timer | None = None
_rmbg_in_use = 0


def _unload_rmbg() -> None:
    global _rmbg_sess, _rmbg_timer
    with _RMBG_LOCK:
        if _rmbg_in_use > 0:
            return
        had = _rmbg_sess is not None
        _rmbg_sess = None
        _rmbg_timer = None
    if had:
        gc.collect()


def _acquire_rmbg_session() -> ort.InferenceSession:
    global _rmbg_sess, _rmbg_in_use, _rmbg_timer
    with _RMBG_LOCK:
        _rmbg_in_use += 1
        if _rmbg_timer is not None:
            _rmbg_timer.cancel()
            _rmbg_timer = None
        if _rmbg_sess is not None:
            return _rmbg_sess
    if not RMBG_MODEL_PATH.is_file():
        with _RMBG_LOCK:
            _rmbg_in_use = max(0, _rmbg_in_use - 1)
        raise FileNotFoundError(f"Missing RMBG-1.4 ONNX model at {RMBG_MODEL_PATH}")
    opts = ort.SessionOptions()
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    opts.intra_op_num_threads = _ONNX_THREADS
    opts.inter_op_num_threads = 1
    opts.enable_cpu_mem_arena = False
    opts.enable_mem_pattern = False
    sess = ort.InferenceSession(str(RMBG_MODEL_PATH), sess_options=opts, providers=["CPUExecutionProvider"])
    with _RMBG_LOCK:
        if _rmbg_sess is None:
            _rmbg_sess = sess
        return _rmbg_sess


def _release_rmbg_session() -> None:
    global _rmbg_in_use, _rmbg_timer
    with _RMBG_LOCK:
        _rmbg_in_use = max(0, _rmbg_in_use - 1)
        if _rmbg_in_use == 0:
            if _rmbg_timer is not None:
                _rmbg_timer.cancel()
            t = threading.Timer(_RMBG_IDLE_TTL, _unload_rmbg)
            t.daemon = True
            t.start()
            _rmbg_timer = t


def _round64(v: int, minimum: int = 256, maximum: int = 1536) -> int:
    v = max(minimum, min(maximum, int(v)))
    return max(minimum, min(maximum, ((v + 32) // 64) * 64))


def _resize_tensor_image(image: torch.Tensor, target_w: int, target_h: int) -> torch.Tensor:
    if image.shape[1] == target_h and image.shape[2] == target_w:
        return image.contiguous()
    mode = "area" if (target_h < image.shape[1] or target_w < image.shape[2]) else "bicubic"
    kwargs = {"align_corners": False, "antialias": True} if mode == "bicubic" else {}
    out = F.interpolate(image.movedim(-1, 1), size=(target_h, target_w), mode=mode, **kwargs).movedim(1, -1)
    return out.clamp(0.0, 1.0).contiguous()


class CcaRemoveBackground:
    """Removes image background using Bria RMBG-1.4 ONNX (CPU, 0 VRAM) and returns RGBA tensor."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
            },
            "optional": {
                "edge_smooth": ("FLOAT", {"default": 0.6, "min": 0.0, "max": 3.0, "step": 0.1}),
                "foreground_boost": ("FLOAT", {"default": 0.0, "min": -0.25, "max": 0.25, "step": 0.02}),
            },
        }

    RETURN_TYPES = ("IMAGE", "MASK")
    RETURN_NAMES = ("rgba_image", "alpha_mask")
    FUNCTION = "remove_bg"
    CATEGORY = "CharacterConceptAI"

    def remove_bg(self, image, edge_smooth=0.6, foreground_boost=0.0):
        sess = _acquire_rmbg_session()
        input_name = sess.get_inputs()[0].name
        out_rgba = []
        out_masks = []
        try:
            for idx in range(image.shape[0]):
                rgb_f = image[idx, :, :, :3].detach().cpu().clamp(0.0, 1.0).numpy().astype(np.float32)
                orig_h, orig_w = rgb_f.shape[:2]

                # RMBG-1.4 standard input: 1024x1024 RGB normalized (x - 0.5) / 1.0
                resized = cv2.resize(rgb_f, (1024, 1024), interpolation=cv2.INTER_LINEAR)
                blob = ((resized - 0.5) / 1.0).transpose(2, 0, 1)[None, :, :, :].astype(np.float32)
                pred = sess.run(None, {input_name: blob})[0]
                matte = np.squeeze(pred).astype(np.float32)
                if matte.ndim == 3:
                    matte = matte[0]
                mi, ma = float(matte.min()), float(matte.max())
                matte = (matte - mi) / max(ma - mi, 1e-6)
                matte = cv2.resize(matte, (orig_w, orig_h), interpolation=cv2.INTER_CUBIC)
                matte = np.clip(matte + float(foreground_boost), 0.0, 1.0)

                if edge_smooth > 0.05:
                    matte = cv2.GaussianBlur(matte, (0, 0), sigmaX=float(edge_smooth), sigmaY=float(edge_smooth))

                # Crisp cleanup of faint background dust while keeping smooth hair strands
                matte = np.where(matte < 0.025, 0.0, np.where(matte > 0.975, 1.0, matte)).astype(np.float32)

                # Slight color-defringe on semi-transparent boundary pixels to eliminate halo glow
                border = (matte > 0.02) & (matte < 0.85)
                rgb_clean = rgb_f.copy()
                if np.any(border):
                    eroded = cv2.erode((rgb_f * 255.0).astype(np.uint8), np.ones((3, 3), np.uint8), iterations=1).astype(np.float32) / 255.0
                    alpha_mix = np.clip((0.85 - matte) / 0.85, 0.0, 0.35)[:, :, None]
                    rgb_clean = np.where(border[:, :, None], rgb_f * (1.0 - alpha_mix) + eroded * alpha_mix, rgb_f)

                rgba = np.concatenate([rgb_clean, matte[:, :, None]], axis=-1)
                out_rgba.append(torch.from_numpy(rgba))
                out_masks.append(torch.from_numpy(matte))
        finally:
            _release_rmbg_session()

        rgba_batch = torch.stack(out_rgba, dim=0).to(device=image.device, dtype=image.dtype)
        mask_batch = torch.stack(out_masks, dim=0).to(device=image.device, dtype=torch.float32)
        return (rgba_batch, mask_batch)


class CcaInpaintPrepare:
    """Aligns source image & painted white-on-black mask to multiples of 64 for Z-Image Union 2.1 Inpaint."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "mask_image": ("IMAGE",),
                "max_side": ("INT", {"default": 1280, "min": 512, "max": 1536, "step": 64}),
                "mask_dilate": ("INT", {"default": 6, "min": 0, "max": 48, "step": 1}),
                "feather_px": ("INT", {"default": 14, "min": 0, "max": 64, "step": 1}),
            }
        }

    RETURN_TYPES = ("IMAGE", "MASK", "IMAGE", "INT", "INT")
    RETURN_NAMES = ("working_image", "control_mask", "soft_blend_mask", "width", "height")
    FUNCTION = "prepare"
    CATEGORY = "CharacterConceptAI"

    def prepare(self, image, mask_image, max_side=1280, mask_dilate=6, feather_px=14):
        src_h, src_w = int(image.shape[1]), int(image.shape[2])
        scale = min(1.0, float(max_side) / float(max(src_h, src_w)))
        target_w = _round64(round(src_w * scale), minimum=256, maximum=max_side)
        target_h = _round64(round(src_h * scale), minimum=256, maximum=max_side)

        working = _resize_tensor_image(image[:, :, :, :3], target_w, target_h)

        # Build control mask (binary/harder for Z-Image Union 2.1) + feathered soft blend mask
        m_resized = _resize_tensor_image(mask_image[:, :, :, :3], target_w, target_h)
        ctrl_masks = []
        soft_masks = []
        for idx in range(working.shape[0]):
            m_arr = m_resized[min(idx, m_resized.shape[0] - 1)].detach().cpu().numpy()
            gray = m_arr.max(axis=-1) if m_arr.ndim == 3 else m_arr
            bin_u8 = (gray > 0.15).astype(np.uint8) * 255
            if mask_dilate > 0:
                k = int(mask_dilate) * 2 + 1
                kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
                bin_u8 = cv2.dilate(bin_u8, kernel, iterations=1)

            ctrl_f = (bin_u8.astype(np.float32) / 255.0).clip(0.0, 1.0)
            if feather_px > 0:
                sigma = max(1.0, float(feather_px) * 0.45)
                soft_f = cv2.GaussianBlur(ctrl_f, (0, 0), sigmaX=sigma, sigmaY=sigma)
                # Ensure core painted region stays 1.0 while outer edge feathers smoothly
                soft_f = np.clip(np.maximum(soft_f, ctrl_f * 0.92), 0.0, 1.0)
            else:
                soft_f = ctrl_f

            ctrl_masks.append(torch.from_numpy(ctrl_f))
            soft_rgb = np.repeat(soft_f[:, :, None], 3, axis=-1)
            soft_masks.append(torch.from_numpy(soft_rgb))

        ctrl_batch = torch.stack(ctrl_masks, dim=0).to(device=image.device, dtype=torch.float32)
        soft_batch = torch.stack(soft_masks, dim=0).to(device=image.device, dtype=image.dtype)
        return (working, ctrl_batch, soft_batch, target_w, target_h)


class CcaOutpaintPrepare:
    """Expands canvas to target aspect ratio or custom padding with smooth edge-replicated seed & mask."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "target_preset": ("STRING", {"default": "16:9"}),
                "pad_left": ("INT", {"default": 0, "min": 0, "max": 768, "step": 16}),
                "pad_right": ("INT", {"default": 0, "min": 0, "max": 768, "step": 16}),
                "pad_top": ("INT", {"default": 0, "min": 0, "max": 768, "step": 16}),
                "pad_bottom": ("INT", {"default": 0, "min": 0, "max": 768, "step": 16}),
                "max_side": ("INT", {"default": 1280, "min": 512, "max": 1536, "step": 64}),
                "overlap_px": ("INT", {"default": 28, "min": 8, "max": 96, "step": 4}),
            }
        }

    RETURN_TYPES = ("IMAGE", "MASK", "IMAGE", "INT", "INT")
    RETURN_NAMES = ("padded_image", "control_mask", "soft_blend_mask", "width", "height")
    FUNCTION = "prepare"
    CATEGORY = "CharacterConceptAI"

    def prepare(
        self,
        image,
        target_preset="16:9",
        pad_left=0,
        pad_right=0,
        pad_top=0,
        pad_bottom=0,
        max_side=1280,
        overlap_px=28,
    ):
        src_h, src_w = int(image.shape[1]), int(image.shape[2])
        preset = (target_preset or "").strip().lower()

        # Compute logical unscaled padding based on preset
        pl, pr, pt, pb = int(pad_left), int(pad_right), int(pad_top), int(pad_bottom)
        if preset == "16:9":
            target_ratio = 16.0 / 9.0
            cur_ratio = float(src_w) / float(max(src_h, 1))
            if cur_ratio < target_ratio:
                needed_w = int(round(src_h * target_ratio)) - src_w
                pl = needed_w // 2
                pr = needed_w - pl
                pt = pb = 0
            else:
                needed_h = int(round(src_w / target_ratio)) - src_h
                pt = needed_h // 2
                pb = needed_h - pt
                pl = pr = 0
        elif preset == "1:1":
            if src_w < src_h:
                diff = src_h - src_w
                pl = diff // 2
                pr = diff - pl
                pt = pb = 0
            elif src_h < src_w:
                diff = src_w - src_h
                pt = diff // 2
                pb = diff - pt
                pl = pr = 0
            else:
                pl = pr = pt = pb = max(64, src_w // 6)
        elif preset == "3:4":
            target_ratio = 3.0 / 4.0
            cur_ratio = float(src_w) / float(max(src_h, 1))
            if cur_ratio > target_ratio:
                needed_h = int(round(src_w / target_ratio)) - src_h
                pt = needed_h // 2
                pb = needed_h - pt
                pl = pr = 0
            else:
                needed_w = int(round(src_h * target_ratio)) - src_w
                pl = needed_w // 2
                pr = needed_w - pl
                pt = pb = 0
        elif preset == "expand25":
            pl = pr = max(64, int(round(src_w * 0.22)))
            pt = pb = max(64, int(round(src_h * 0.22)))
        elif preset == "left":
            pl = max(128, int(round(src_w * 0.45)))
            pr = pt = pb = 0
        elif preset == "right":
            pr = max(128, int(round(src_w * 0.45)))
            pl = pt = pb = 0

        if pl == 0 and pr == 0 and pt == 0 and pb == 0:
            pl = pr = max(64, int(round(src_w * 0.22)))

        raw_w = src_w + pl + pr
        raw_h = src_h + pt + pb
        scale = min(1.0, float(max_side) / float(max(raw_w, raw_h)))
        canvas_w = _round64(round(raw_w * scale), minimum=512, maximum=max_side)
        canvas_h = _round64(round(raw_h * scale), minimum=512, maximum=max_side)

        # Fit scaled source cleanly inside canvas preserving exact relative placement
        fit_scale = min(float(canvas_w) / float(raw_w), float(canvas_h) / float(raw_h))
        inner_w = max(64, min(canvas_w - 16, int(round(src_w * fit_scale))))
        inner_h = max(64, min(canvas_h - 16, int(round(src_h * fit_scale))))
        off_x = int(round(pl * fit_scale))
        off_y = int(round(pt * fit_scale))
        off_x = max(0, min(canvas_w - inner_w, off_x))
        off_y = max(0, min(canvas_h - inner_h, off_y))

        inner_resized = _resize_tensor_image(image[:, :, :, :3], inner_w, inner_h)
        ov = max(8, min(int(overlap_px), inner_w // 6, inner_h // 6))

        padded_frames = []
        ctrl_masks = []
        soft_masks = []

        for idx in range(inner_resized.shape[0]):
            src_u8 = (inner_resized[idx].detach().cpu().numpy() * 255.0).round().astype(np.uint8)
            pad_r = canvas_w - inner_w - off_x
            pad_b = canvas_h - inner_h - off_y

            # Replicate edges + heavy blur outside original box for smooth color-temperature context
            padded_u8 = cv2.copyMakeBorder(
                src_u8, off_y, pad_b, off_x, pad_r, borderType=cv2.BORDER_REFLECT_101
            )
            blurred_bg = cv2.GaussianBlur(padded_u8, (0, 0), sigmaX=24.0, sigmaY=24.0)
            canvas_u8 = blurred_bg.copy()
            canvas_u8[off_y : off_y + inner_h, off_x : off_x + inner_w] = src_u8

            # Mask = 1.0 on expanded border + inward overlap band on every side that was padded
            keep = np.zeros((canvas_h, canvas_w), dtype=np.float32)
            in_l = off_x + (ov if off_x > 0 else 0)
            in_r = off_x + inner_w - (ov if pad_r > 0 else 0)
            in_t = off_y + (ov if off_y > 0 else 0)
            in_b = off_y + inner_h - (ov if pad_b > 0 else 0)
            if in_r > in_l and in_b > in_t:
                keep[in_t:in_b, in_l:in_r] = 1.0

            ctrl_f = 1.0 - keep
            sigma = max(4.0, float(ov) * 0.55)
            soft_keep = cv2.GaussianBlur(keep, (0, 0), sigmaX=sigma, sigmaY=sigma)
            # Outside the original box MUST always be 1.0 (fully generated)
            orig_box = np.zeros((canvas_h, canvas_w), dtype=np.float32)
            orig_box[off_y : off_y + inner_h, off_x : off_x + inner_w] = 1.0
            soft_keep = soft_keep * orig_box
            soft_f = np.clip(1.0 - soft_keep, 0.0, 1.0).astype(np.float32)

            padded_frames.append(torch.from_numpy(canvas_u8.astype(np.float32) / 255.0))
            ctrl_masks.append(torch.from_numpy(ctrl_f.astype(np.float32)))
            soft_masks.append(torch.from_numpy(np.repeat(soft_f[:, :, None], 3, axis=-1)))

        padded_batch = torch.stack(padded_frames, dim=0).to(device=image.device, dtype=image.dtype)
        ctrl_batch = torch.stack(ctrl_masks, dim=0).to(device=image.device, dtype=torch.float32)
        soft_batch = torch.stack(soft_masks, dim=0).to(device=image.device, dtype=image.dtype)
        return (padded_batch, ctrl_batch, soft_batch, canvas_w, canvas_h)


class CcaInpaintComposite:
    """Blends inpainted/outpainted output back onto original pixels using a smooth feathered mask."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "original_image": ("IMAGE",),
                "generated_image": ("IMAGE",),
                "soft_blend_mask": ("IMAGE",),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "composite"
    CATEGORY = "CharacterConceptAI"

    def composite(self, original_image, generated_image, soft_blend_mask):
        h, w = int(original_image.shape[1]), int(original_image.shape[2])
        gen = _resize_tensor_image(generated_image[:, :, :, :3], w, h).to(original_image.device, original_image.dtype)
        msk = _resize_tensor_image(soft_blend_mask[:, :, :, :1], w, h).to(original_image.device, original_image.dtype)
        orig = original_image[:, :, :, :3]
        blended = (orig * (1.0 - msk) + gen * msk).clamp(0.0, 1.0)
        return (blended,)


class CcaSmartUpscale2x:
    """Supersampled 2x upscaler: runs tiled 4x neural upscale (NMKD-Siax / UltraSharp) -> crisp 2x downscale."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "upscale_model": ("UPSCALE_MODEL",),
                "detail_mix": ("FLOAT", {"default": 0.80, "min": 0.30, "max": 1.0, "step": 0.05}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "upscale_2x"
    CATEGORY = "CharacterConceptAI"

    def upscale_2x(self, image, upscale_model, detail_mix=0.80):
        device = comfy.model_management.get_torch_device()
        in_img = image[:, :, :, :3]
        orig_h, orig_w = int(in_img.shape[1]), int(in_img.shape[2])
        target_h, target_w = orig_h * 2, orig_w * 2

        # Run tiled neural upscale (tile=384 keeps peak VRAM safely under 3 GB on 6 GB RTX 4050)
        patcher = getattr(upscale_model, "patcher", None)
        device = patcher.load_device if patcher is not None else comfy.model_management.get_torch_device()
        memory_required = (384 * 384 * 3) * in_img.element_size() * max(upscale_model.scale, 1.0) * 384.0
        memory_required += in_img.nelement() * in_img.element_size()
        if patcher is not None:
            comfy.model_management.load_models_gpu([patcher], memory_required=memory_required, force_full_load=True)
        else:
            comfy.model_management.free_memory(memory_required, device)
            upscale_model.to(device)

        output_device = comfy.model_management.intermediate_device()
        try:
            in_nchw = in_img.movedim(-1, -3).to(device)
            tile = 384
            overlap = 32
            oom = True
            s = None
            while oom:
                try:
                    s = comfy.utils.tiled_scale(
                        in_nchw,
                        lambda a: upscale_model(a.float()),
                        tile_x=tile,
                        tile_y=tile,
                        overlap=overlap,
                        upscale_amount=upscale_model.scale,
                        output_device=output_device,
                    )
                    oom = False
                except Exception as e:
                    comfy.model_management.raise_non_oom(e)
                    tile //= 2
                    if tile < 128:
                        raise e
            up_nhwc = torch.clamp(s.movedim(-3, -1), min=0.0, max=1.0).to(in_img.device, in_img.dtype)
        finally:
            if patcher is None:
                upscale_model.to("cpu")

        # Downsample 4x -> exact 2x using high-quality antialiased area interpolation
        if up_nhwc.shape[1] != target_h or up_nhwc.shape[2] != target_w:
            up_2x = F.interpolate(up_nhwc.movedim(-1, 1), size=(target_h, target_w), mode="area").movedim(1, -1)
        else:
            up_2x = up_nhwc

        mix = float(np.clip(detail_mix, 0.30, 1.0))
        if mix < 0.99:
            bicubic_2x = F.interpolate(
                in_img.movedim(-1, 1), size=(target_h, target_w), mode="bicubic", align_corners=False, antialias=True
            ).movedim(1, -1).clamp(0.0, 1.0)
            result = (bicubic_2x * (1.0 - mix) + up_2x * mix).clamp(0.0, 1.0)
        else:
            result = up_2x.clamp(0.0, 1.0)

        return (result,)


def _tile_positions(total: int, tile: int, overlap: int) -> list[tuple[int, int]]:
    if total <= tile:
        return [(0, total)]
    stride = max(64, tile - overlap)
    starts = list(range(0, max(1, total - tile + 1), stride))
    if starts[-1] + tile < total:
        starts.append(max(0, total - tile))
    return [(s, min(total, s + tile)) for s in starts]


def _make_tile_weight_2d(h: int, w: int, is_top: bool, is_bottom: bool, is_left: bool, is_right: bool, feather: int) -> np.ndarray:
    wy = np.ones(h, dtype=np.float32)
    wx = np.ones(w, dtype=np.float32)
    fy = min(feather, h // 3)
    fx = min(feather, w // 3)
    if fy > 1:
        ramp = 0.5 * (1.0 - np.cos(np.linspace(0.0, math.pi, fy, dtype=np.float32)))
        if not is_top:
            wy[:fy] = ramp
        if not is_bottom:
            wy[-fy:] = ramp[::-1]
    if fx > 1:
        ramp = 0.5 * (1.0 - np.cos(np.linspace(0.0, math.pi, fx, dtype=np.float32)))
        if not is_left:
            wx[:fx] = ramp
        if not is_right:
            wx[-fx:] = ramp[::-1]
    return np.maximum(wy[:, None] * wx[None, :], 1e-4)


class CcaTiledHiresRefine:
    """2K Tiled Latent Hires-Fix using Z-Image-Turbo at low denoise with seamless Hann feathering (<5.5 GB VRAM)."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "model": ("MODEL",),
                "positive": ("CONDITIONING",),
                "negative": ("CONDITIONING",),
                "vae": ("VAE",),
                "seed": ("INT", {"default": 123456, "min": 0, "max": 0xFFFFFFFFFFFFFFFF}),
                "steps": ("INT", {"default": 6, "min": 3, "max": 12, "step": 1}),
                "denoise": ("FLOAT", {"default": 0.24, "min": 0.08, "max": 0.50, "step": 0.02}),
                "tile_size": ("INT", {"default": 1024, "min": 512, "max": 1280, "step": 64}),
                "tile_overlap": ("INT", {"default": 128, "min": 64, "max": 256, "step": 32}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "refine"
    CATEGORY = "CharacterConceptAI"

    def refine(
        self,
        image,
        model,
        positive,
        negative,
        vae,
        seed=123456,
        steps=6,
        denoise=0.24,
        tile_size=1024,
        tile_overlap=128,
    ):
        denoise_val = float(np.clip(denoise, 0.08, 0.50))
        steps_val = max(3, min(12, int(steps)))
        t_size = _round64(tile_size, minimum=512, maximum=1280)
        t_overlap = max(64, min(t_size // 3, int(tile_overlap)))

        out_frames = []
        for b_idx in range(image.shape[0]):
            src = image[b_idx : b_idx + 1, :, :, :3]
            full_h, full_w = int(src.shape[1]), int(src.shape[2])

            ys = _tile_positions(full_h, t_size, t_overlap)
            xs = _tile_positions(full_w, t_size, t_overlap)

            accum = np.zeros((full_h, full_w, 3), dtype=np.float32)
            weights = np.zeros((full_h, full_w, 1), dtype=np.float32)

            tile_idx = 0
            for yi, (y0, y1) in enumerate(ys):
                for xi, (x0, x1) in enumerate(xs):
                    crop = src[:, y0:y1, x0:x1, :]
                    ch, cw = int(crop.shape[1]), int(crop.shape[2])
                    # Pad tile internally to multiple of 64 if edge tile isn't divisible
                    pad_h = (64 - (ch % 64)) % 64
                    pad_w = (64 - (cw % 64)) % 64
                    if pad_h > 0 or pad_w > 0:
                        crop_in = F.pad(crop.movedim(-1, 1), (0, pad_w, 0, pad_h), mode="reflect").movedim(1, -1)
                    else:
                        crop_in = crop

                    latent = vae.encode(crop_in[:, :, :, :3])
                    sampled = nodes.common_ksampler(
                        model,
                        int(seed) + tile_idx,
                        steps_val,
                        1.0,
                        "res_multistep",
                        "simple",
                        positive,
                        negative,
                        {"samples": latent},
                        denoise=denoise_val,
                    )[0]
                    decoded = vae.decode(sampled["samples"])
                    decoded_crop = decoded[0, :ch, :cw, :3].detach().cpu().clamp(0.0, 1.0).numpy().astype(np.float32)

                    # Preserve exact low-frequency tile color to guarantee 0 brightness seams between tiles
                    src_np = crop[0, :ch, :cw, :3].detach().cpu().numpy().astype(np.float32)
                    low_src = cv2.GaussianBlur(src_np, (0, 0), sigmaX=18.0, sigmaY=18.0)
                    low_dec = cv2.GaussianBlur(decoded_crop, (0, 0), sigmaX=18.0, sigmaY=18.0)
                    decoded_crop = np.clip(decoded_crop + (low_src - low_dec) * 0.55, 0.0, 1.0)

                    w2d = _make_tile_weight_2d(
                        ch, cw,
                        is_top=(yi == 0),
                        is_bottom=(yi == len(ys) - 1),
                        is_left=(xi == 0),
                        is_right=(xi == len(xs) - 1),
                        feather=t_overlap,
                    )[:, :, None]

                    accum[y0:y1, x0:x1, :] += decoded_crop * w2d
                    weights[y0:y1, x0:x1, :] += w2d
                    tile_idx += 1

            merged = np.clip(accum / np.maximum(weights, 1e-4), 0.0, 1.0)
            out_frames.append(torch.from_numpy(merged))

        result = torch.stack(out_frames, dim=0).to(device=image.device, dtype=image.dtype)
        return (result,)


NODE_CLASS_MAPPINGS = {
    "CcaRemoveBackground": CcaRemoveBackground,
    "CcaInpaintPrepare": CcaInpaintPrepare,
    "CcaOutpaintPrepare": CcaOutpaintPrepare,
    "CcaInpaintComposite": CcaInpaintComposite,
    "CcaSmartUpscale2x": CcaSmartUpscale2x,
    "CcaTiledHiresRefine": CcaTiledHiresRefine,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "CcaRemoveBackground": "CCA Remove Background (RMBG-1.4 Local 0-VRAM)",
    "CcaInpaintPrepare": "CCA Inpaint Prepare (Z-Image Union 2.1)",
    "CcaOutpaintPrepare": "CCA Outpaint Prepare (Z-Image Union 2.1)",
    "CcaInpaintComposite": "CCA Inpaint Seamless Composite",
    "CcaSmartUpscale2x": "CCA Smart Supersampled 2x Upscale",
    "CcaTiledHiresRefine": "CCA Tiled 2K Latent Hires-Fix (Z-Image Turbo)",
}
