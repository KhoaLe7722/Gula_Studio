"""Scheduled Depth ControlNet & Seamless Letterbox Preprocessor for Z-Image-Turbo (Dreamina Studio).

Fixes three critical failure modes when driving Z-Image-Turbo-Fun-Controlnet-Union-2.1 with
3D mannequin depth maps (e.g. 900x1200 portrait depth maps from ai-pose-gallery-zh):
  1. Center-crop amputation at 1:1 / 16:9 -> fixed by CcaDepthLetterboxResize (contain-fit +
     seamless cv2.BORDER_REPLICATE background depth extension + safety breathing margin).
  2. Bald plastic egg-head & mannequin ball-joints burned into final image -> fixed by
     gentle Gaussian depth softening + sigma-scheduled early cutoff (end_percent ~ 0.55)
     so ControlNet locks 3D pose & camera angle in the early layout steps, then releases
     Z-Image-Turbo in the final detail steps to paint natural hair, face, eyes, and anatomy.
  3. Zero extra VRAM — actually ~20% faster because ControlNet blocks skip late detail steps.
"""

from __future__ import annotations

import cv2
import numpy as np
import torch

import comfy.ldm.lumina.controlnet
from comfy_extras.nodes_model_patch import ZImageControlPatch


class CcaDepthLetterboxResize:
    """Contain-fits a depth pose image to target (width, height) with seamless edge replication."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "width": ("INT", {"default": 896, "min": 256, "max": 2048, "step": 64}),
                "height": ("INT", {"default": 1152, "min": 256, "max": 2048, "step": 64}),
                "margin_percent": ("FLOAT", {"default": 0.04, "min": 0.0, "max": 0.25, "step": 0.01}),
                "blur_radius": ("FLOAT", {"default": 2.2, "min": 0.0, "max": 12.0, "step": 0.1}),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "prepare"
    CATEGORY = "CharacterConceptAI"

    def prepare(self, image, width=896, height=1152, margin_percent=0.04, blur_radius=2.2):
        margin = float(np.clip(margin_percent, 0.0, 0.25))
        blur = float(np.clip(blur_radius, 0.0, 12.0))
        out_frames = []

        usable_w = max(64, int(round(width * (1.0 - 2.0 * margin))))
        usable_h = max(64, int(round(height * (1.0 - 2.0 * margin))))

        for idx in range(image.shape[0]):
            arr = (image[idx].detach().cpu().clamp(0, 1).numpy() * 255.0).round().astype(np.uint8)

            # All 1,439 poses in ai-pose-gallery-zh use standard white=near / black=far depth encoding.
            # Gently expand severely washed-out depth range while preserving natural smooth floor gradients.
            gray = cv2.cvtColor(arr, cv2.COLOR_RGB2GRAY) if arr.ndim == 3 else arr
            p01, p99 = float(np.percentile(gray, 1)), float(np.percentile(gray, 99))
            if 10.0 < (p99 - p01) < 115.0:
                arr_f = np.clip((arr.astype(np.float32) - p01) / (p99 - p01), 0.0, 1.0)
                arr = (arr_f * 238.0 + 12.0).round().astype(np.uint8)

            src_h, src_w = arr.shape[:2]
            scale = min(float(usable_w) / float(src_w), float(usable_h) / float(src_h))
            fit_w = max(1, min(width, int(round(src_w * scale))))
            fit_h = max(1, min(height, int(round(src_h * scale))))
            interp = cv2.INTER_AREA if scale < 1.0 else cv2.INTER_CUBIC
            resized = cv2.resize(arr, (fit_w, fit_h), interpolation=interp)

            pad_left = (width - fit_w) // 2
            pad_right = width - fit_w - pad_left
            pad_top = (height - fit_h) // 2
            pad_bottom = height - fit_h - pad_top

            # Edge replication seamlessly extends the smooth depth background gradient with zero box seams.
            padded = cv2.copyMakeBorder(
                resized,
                pad_top,
                pad_bottom,
                pad_left,
                pad_right,
                borderType=cv2.BORDER_REPLICATE,
            )

            if blur > 0.05:
                scaled_sigma = blur * (min(width, height) / 1024.0)
                if scaled_sigma >= 0.35:
                    padded = cv2.GaussianBlur(padded, (0, 0), sigmaX=scaled_sigma, sigmaY=scaled_sigma)

            out_frames.append(torch.from_numpy(padded.astype(np.float32) / 255.0))

        batch = torch.stack(out_frames, dim=0).to(device=image.device, dtype=image.dtype)
        return (batch,)


class ScheduledZImageControlPatch(ZImageControlPatch):
    """Wraps ComfyUI's ZImageControlPatch with sigma-based smooth cosine ramp-down cutoff.

    Keeps full ControlNet strength through the pose-locking phase (0..ramp_start_percent),
    smoothly tapers strength down to 0 by end_percent so the body pose never drifts, and
    turns completely off for the final fine-detail steps so Z-Image paints natural hair,
    human facial features, and realistic skin instead of bald mannequin surfaces.
    """

    def __init__(
        self,
        model_patch,
        vae,
        image,
        strength: float,
        sigma_start: float,
        sigma_ramp_start: float,
        sigma_end: float,
        inpaint_image=None,
        mask=None,
    ):
        super().__init__(model_patch, vae, image, strength, inpaint_image=inpaint_image, mask=mask)
        self.base_strength = float(strength)
        self.sigma_start = float(sigma_start)
        self.sigma_ramp_start = float(sigma_ramp_start)
        self.sigma_end = float(sigma_end)

    def __call__(self, kwargs):
        transformer_options = kwargs.get("transformer_options", {})
        sigmas = transformer_options.get("sigmas", None)
        if sigmas is not None:
            sigma = float(sigmas[0].item())
            if sigma > self.sigma_start + 1e-5 or sigma < self.sigma_end - 1e-5:
                self.temp_data = None
                kwargs.pop("img", None)
                kwargs.pop("txt", None)
                return kwargs
            if sigma < self.sigma_ramp_start and self.sigma_ramp_start > self.sigma_end + 1e-5:
                t = (sigma - self.sigma_end) / (self.sigma_ramp_start - self.sigma_end)
                t = float(np.clip(t, 0.0, 1.0))
                # Smooth cosine taper from 1.0 down to 0.0
                scale = 0.5 * (1.0 - float(np.cos(np.pi * t)))
                self.strength = self.base_strength * scale
            else:
                self.strength = self.base_strength
        else:
            self.strength = self.base_strength
        return super().__call__(kwargs)


class CcaZImageFunControlnetScheduled:
    """Applies Z-Image Fun ControlNet with sigma-based smooth ramp-down cutoff."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "model": ("MODEL",),
                "model_patch": ("MODEL_PATCH",),
                "vae": ("VAE",),
                "image": ("IMAGE",),
                "strength": ("FLOAT", {"default": 0.78, "min": 0.0, "max": 2.0, "step": 0.01}),
                "start_percent": ("FLOAT", {"default": 0.0, "min": 0.0, "max": 1.0, "step": 0.01}),
                "end_percent": ("FLOAT", {"default": 0.76, "min": 0.10, "max": 1.0, "step": 0.01}),
            }
        }

    RETURN_TYPES = ("MODEL",)
    FUNCTION = "apply"
    CATEGORY = "CharacterConceptAI"

    def apply(self, model, model_patch, vae, image, strength=0.78, start_percent=0.0, end_percent=0.76):
        if not isinstance(model_patch.model, comfy.ldm.lumina.controlnet.ZImage_Control):
            raise ValueError("Model patch is not a Z-Image ControlNet patch.")

        model_patched = model.clone()
        model_sampling = model.get_model_object("model_sampling")
        sp = float(np.clip(start_percent, 0.0, 1.0))
        ep = float(np.clip(end_percent, 0.10, 1.0))
        # Hold full strength for ~65% of the active window, then cosine-taper smoothly to 0 by end_percent
        rp = sp + (ep - sp) * 0.65

        sigma_start = float(model_sampling.percent_to_sigma(sp))
        sigma_ramp_start = float(model_sampling.percent_to_sigma(rp))
        sigma_end = float(model_sampling.percent_to_sigma(ep))

        rgb = image[:, :, :, :3]
        patch = ScheduledZImageControlPatch(
            model_patch=model_patch,
            vae=vae,
            image=rgb,
            strength=float(strength),
            sigma_start=sigma_start,
            sigma_ramp_start=sigma_ramp_start,
            sigma_end=sigma_end,
        )
        model_patched.set_model_noise_refiner_patch(patch)
        model_patched.set_model_double_block_patch(patch)
        return (model_patched,)


NODE_CLASS_MAPPINGS = {
    "CcaDepthLetterboxResize": CcaDepthLetterboxResize,
    "CcaZImageFunControlnetScheduled": CcaZImageFunControlnetScheduled,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "CcaDepthLetterboxResize": "CCA Depth Letterbox Resize (No-Crop)",
    "CcaZImageFunControlnetScheduled": "CCA Z-Image ControlNet (Scheduled)",
}
