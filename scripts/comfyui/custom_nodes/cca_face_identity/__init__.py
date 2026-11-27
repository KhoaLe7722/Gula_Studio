"""Local offline multi-angle face identity consistency node for Dreamina Studio (CharacterConceptAI).

Pipeline (0 MB GPU VRAM via ONNX Runtime CPU — zero conflict with RTX 4050 6 GB):
  1. SCRFD 10G (det_10g.onnx) + YuNet fallback -> 5-point facial landmarks + head yaw estimation
  2. ArcFace WebFace600K (w600k_r50.onnx) -> 512-dim identity embeddings across 1..5 reference angles/expressions
  3. Yaw-Aware Multi-Image Centroid Fusion -> L2-normalized weighted consensus embedding prioritizing matching head turn
  4. FaceFusion HyperSwap 1C 256 (hyperswap_1c_256.onnx, 256x256 native + learned hair/occlusion mask)
     with automatic fallback to InsightFace InSwapper 128 (inswapper_128.onnx + emap)
  5. Gentle GFPGAN v1.4 (GFPGANv1.4.onnx, 512x512) -> subtle crisp skin/eye finishing without doll-face drift
  6. Soft LAB scene-lighting match + smooth feathered composite
"""

from __future__ import annotations

from collections import OrderedDict
from functools import lru_cache
import gc
import hashlib
import os
from pathlib import Path
import threading

import cv2
import numpy as np
import onnxruntime as ort
import torch
from skimage import transform as sktransform


ROOT = Path(__file__).resolve().parent
COMFY_ROOT = ROOT.parent.parent
MODELS_DIR = COMFY_ROOT / "models" / "cca_face_identity"
YUNET_PATH = COMFY_ROOT / "custom_nodes" / "cca_face" / "face_detection_yunet_2023mar.onnx"

ARCFACE_DST_112 = np.array(
    [
        [38.2946, 51.6963],
        [73.5318, 51.5014],
        [56.0252, 71.7366],
        [41.5493, 92.3655],
        [70.7299, 92.2041],
    ],
    dtype=np.float32,
)

FFHQ_DST_512 = np.array(
    [
        [192.98138, 239.94708],
        [318.90277, 240.19360],
        [256.63416, 314.01935],
        [201.26117, 371.41043],
        [313.08905, 371.15118],
    ],
    dtype=np.float32,
)


_ONNX_INTRA_THREADS = min(8, max(4, (os.cpu_count() or 8) // 2))


def _create_session(path: Path, low_memory: bool = False) -> ort.InferenceSession:
    if not path.is_file():
        raise FileNotFoundError(f"Missing face identity model: {path}")
    opts = ort.SessionOptions()
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    opts.intra_op_num_threads = _ONNX_INTRA_THREADS
    opts.inter_op_num_threads = 1
    if low_memory:
        opts.enable_cpu_mem_arena = False
        opts.enable_mem_pattern = False
    return ort.InferenceSession(str(path), sess_options=opts, providers=["CPUExecutionProvider"])


@lru_cache(maxsize=1)
def _det_session() -> ort.InferenceSession:
    return _create_session(MODELS_DIR / "det_10g.onnx")


@lru_cache(maxsize=1)
def _rec_session() -> ort.InferenceSession:
    return _create_session(MODELS_DIR / "w600k_r50.onnx")


_HEAVY_SESSION_LOCK = threading.Lock()
_HEAVY_IDLE_TTL_SECONDS = 60.0
_cached_hyper_sess: ort.InferenceSession | None = None
_cached_inswap_sess: ort.InferenceSession | None = None
_cached_gfpgan_sess: ort.InferenceSession | None = None
_heavy_unload_timer: threading.Timer | None = None
_heavy_in_use = 0

_SOURCE_EMB_LOCK = threading.Lock()
_SOURCE_EMB_CACHE: OrderedDict[tuple[tuple[int, ...], bytes], tuple[np.ndarray, float, float] | None] = OrderedDict()
_SOURCE_EMB_CACHE_MAX = 32


def _unload_heavy_sessions() -> None:
    global _cached_hyper_sess, _cached_inswap_sess, _cached_gfpgan_sess, _heavy_unload_timer
    with _HEAVY_SESSION_LOCK:
        if _heavy_in_use > 0:
            return
        had_any = (_cached_hyper_sess is not None) or (_cached_inswap_sess is not None) or (_cached_gfpgan_sess is not None)
        _cached_hyper_sess = None
        _cached_inswap_sess = None
        _cached_gfpgan_sess = None
        _heavy_unload_timer = None
    if had_any:
        gc.collect()


def _begin_heavy_use() -> None:
    global _heavy_in_use, _heavy_unload_timer
    with _HEAVY_SESSION_LOCK:
        _heavy_in_use += 1
        if _heavy_unload_timer is not None:
            _heavy_unload_timer.cancel()
            _heavy_unload_timer = None


def _end_heavy_use() -> None:
    global _heavy_in_use, _heavy_unload_timer
    with _HEAVY_SESSION_LOCK:
        _heavy_in_use = max(0, _heavy_in_use - 1)
        if _heavy_in_use == 0:
            if _heavy_unload_timer is not None:
                _heavy_unload_timer.cancel()
            timer = threading.Timer(_HEAVY_IDLE_TTL_SECONDS, _unload_heavy_sessions)
            timer.daemon = True
            timer.start()
            _heavy_unload_timer = timer


def _get_hyperswap_session() -> ort.InferenceSession | None:
    global _cached_hyper_sess
    with _HEAVY_SESSION_LOCK:
        if _cached_hyper_sess is not None:
            return _cached_hyper_sess
    p = MODELS_DIR / "hyperswap_1c_256.onnx"
    if not p.is_file():
        return None
    sess = _create_session(p, low_memory=True)
    with _HEAVY_SESSION_LOCK:
        if _cached_hyper_sess is None:
            _cached_hyper_sess = sess
        return _cached_hyper_sess


def _get_inswapper_session() -> ort.InferenceSession:
    global _cached_inswap_sess
    with _HEAVY_SESSION_LOCK:
        if _cached_inswap_sess is not None:
            return _cached_inswap_sess
    sess = _create_session(MODELS_DIR / "inswapper_128.onnx", low_memory=True)
    with _HEAVY_SESSION_LOCK:
        if _cached_inswap_sess is None:
            _cached_inswap_sess = sess
        return _cached_inswap_sess


def _get_gfpgan_session() -> ort.InferenceSession:
    global _cached_gfpgan_sess
    with _HEAVY_SESSION_LOCK:
        if _cached_gfpgan_sess is not None:
            return _cached_gfpgan_sess
    sess = _create_session(MODELS_DIR / "GFPGANv1.4.onnx", low_memory=True)
    with _HEAVY_SESSION_LOCK:
        if _cached_gfpgan_sess is None:
            _cached_gfpgan_sess = sess
        return _cached_gfpgan_sess


@lru_cache(maxsize=1)
def _emap() -> np.ndarray:
    p = MODELS_DIR / "inswapper_emap.npy"
    if not p.is_file():
        raise FileNotFoundError(f"Missing inswapper_emap.npy at {p}")
    return np.load(str(p)).astype(np.float32)


@lru_cache(maxsize=1)
def _yunet_detector():
    if not YUNET_PATH.is_file():
        return None
    return cv2.FaceDetectorYN.create(str(YUNET_PATH), "", (640, 640), 0.55, 0.3, 50)


def _nms(dets: np.ndarray, thresh: float = 0.4) -> list[int]:
    x1, y1, x2, y2, scores = dets[:, 0], dets[:, 1], dets[:, 2], dets[:, 3], dets[:, 4]
    areas = np.maximum(0.0, x2 - x1 + 1.0) * np.maximum(0.0, y2 - y1 + 1.0)
    order = scores.argsort()[::-1]
    keep = []
    while order.size > 0:
        i = int(order[0])
        keep.append(i)
        xx1 = np.maximum(x1[i], x1[order[1:]])
        yy1 = np.maximum(y1[i], y1[order[1:]])
        xx2 = np.minimum(x2[i], x2[order[1:]])
        yy2 = np.minimum(y2[i], y2[order[1:]])
        w = np.maximum(0.0, xx2 - xx1 + 1.0)
        h = np.maximum(0.0, yy2 - yy1 + 1.0)
        inter = w * h
        ovr = inter / np.maximum(areas[i] + areas[order[1:]] - inter, 1e-6)
        inds = np.where(ovr <= thresh)[0]
        order = order[inds + 1]
    return keep


@lru_cache(maxsize=8)
def _scrfd_anchor_centers(input_size: int, stride: int, num_anchors: int = 2) -> np.ndarray:
    fm_h = input_size // stride
    fm_w = input_size // stride
    centers = np.stack(np.mgrid[:fm_h, :fm_w][::-1], axis=-1).astype(np.float32) * stride
    centers = centers.reshape(-1, 2)
    if num_anchors > 1:
        centers = np.stack([centers] * num_anchors, axis=1).reshape(-1, 2)
    return centers


def _run_scrfd_raw(bgr: np.ndarray, input_size: int = 640):
    h, w = bgr.shape[:2]
    im_ratio = float(h) / float(w)
    if im_ratio > 1.0:
        new_h = input_size
        new_w = max(1, int(round(new_h / im_ratio)))
    else:
        new_w = input_size
        new_h = max(1, int(round(new_w * im_ratio)))
    det_scale = float(new_h) / float(h)
    resized = cv2.resize(bgr, (new_w, new_h), interpolation=cv2.INTER_AREA)
    det_img = np.zeros((input_size, input_size, 3), dtype=np.uint8)
    det_img[:new_h, :new_w, :] = resized

    blob = cv2.dnn.blobFromImage(det_img, 1.0 / 128.0, (input_size, input_size), (127.5, 127.5, 127.5), swapRB=True)
    sess = _det_session()
    input_name = sess.get_inputs()[0].name
    net_outs = sess.run(None, {input_name: blob})
    return net_outs, det_scale


def _decode_scrfd_outputs(net_outs, det_scale: float, input_size: int = 640, thresh: float = 0.45):
    fmc = 3
    strides = (8, 16, 32)
    num_anchors = 2
    scores_list, bboxes_list, kpss_list = [], [], []

    for idx, stride in enumerate(strides):
        scores = net_outs[idx].reshape(-1)
        bbox_preds = net_outs[idx + fmc].reshape(-1, 4) * stride
        kps_preds = net_outs[idx + fmc * 2].reshape(-1, 10) * stride

        anchor_centers = _scrfd_anchor_centers(input_size, stride, num_anchors)

        pos_inds = np.where(scores >= thresh)[0]
        if pos_inds.size == 0:
            continue

        cx = anchor_centers[pos_inds, 0]
        cy = anchor_centers[pos_inds, 1]
        bp = bbox_preds[pos_inds]
        bboxes = np.stack([cx - bp[:, 0], cy - bp[:, 1], cx + bp[:, 2], cy + bp[:, 3]], axis=-1)

        kp = kps_preds[pos_inds]
        kps = np.empty((pos_inds.size, 5, 2), dtype=np.float32)
        for j in range(5):
            kps[:, j, 0] = cx + kp[:, j * 2]
            kps[:, j, 1] = cy + kp[:, j * 2 + 1]

        scores_list.append(scores[pos_inds])
        bboxes_list.append(bboxes / det_scale)
        kpss_list.append(kps / det_scale)

    if not scores_list:
        return None

    scores_all = np.concatenate(scores_list)
    bboxes_all = np.concatenate(bboxes_list, axis=0)
    kpss_all = np.concatenate(kpss_list, axis=0)
    pre_det = np.hstack([bboxes_all, scores_all[:, None]]).astype(np.float32)
    keep = _nms(pre_det, 0.4)

    faces = []
    for k in keep:
        x1, y1, x2, y2 = bboxes_all[k]
        bw, bh = max(1.0, x2 - x1), max(1.0, y2 - y1)
        if bw < 20 or bh < 20:
            continue
        faces.append((bboxes_all[k], kpss_all[k], float(scores_all[k])))
    if not faces:
        return []
    faces.sort(key=lambda f: (f[0][2] - f[0][0]) * (f[0][3] - f[0][1]) * f[2], reverse=True)
    return faces


def _detect_scrfd(bgr: np.ndarray, input_size: int = 640, thresh: float = 0.45):
    net_outs, det_scale = _run_scrfd_raw(bgr, input_size=input_size)
    return _decode_scrfd_outputs(net_outs, det_scale, input_size=input_size, thresh=thresh)


def _detect_yunet_fallback(bgr: np.ndarray):
    detector = _yunet_detector()
    if detector is None:
        return []
    h, w = bgr.shape[:2]
    scale = 640.0 / max(h, w)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    canvas = np.full((640, 640, 3), 114, dtype=np.uint8)
    canvas[:nh, :nw] = cv2.resize(bgr, (nw, nh))
    detector.setScoreThreshold(0.55)
    _, hits = detector.detect(canvas)
    if hits is None or len(hits) == 0:
        return []
    faces = []
    for row in hits:
        x, y, bw, bh = row[:4] / scale
        if bw < 20 or bh < 20:
            continue
        kps = row[4:14].reshape(5, 2) / scale
        bbox = np.array([x, y, x + bw, y + bh], dtype=np.float32)
        faces.append((bbox, kps.astype(np.float32), float(row[-1])))
    faces.sort(key=lambda f: (f[0][2] - f[0][0]) * (f[0][3] - f[0][1]) * f[2], reverse=True)
    return faces


def _detect_all_faces(bgr: np.ndarray):
    # Single SCRFD forward pass shared across 0.45 and 0.28 thresholds (avoids 2x ONNX runs)
    net_outs, det_scale = _run_scrfd_raw(bgr, input_size=640)
    faces = _decode_scrfd_outputs(net_outs, det_scale, input_size=640, thresh=0.45)
    if not faces:
        faces = _decode_scrfd_outputs(net_outs, det_scale, input_size=640, thresh=0.28)
    if not faces:
        faces = _detect_yunet_fallback(bgr)
    return faces


def _detect_primary_face(bgr: np.ndarray):
    faces = _detect_all_faces(bgr)
    return faces[0] if faces else None


def _estimate_yaw_score(kps: np.ndarray) -> float:
    """Rotation-invariant head yaw estimate in [-1, 1] from 5 facial landmarks.
    0.0 = frontal, negative = turned toward viewer-left, positive = turned toward viewer-right.
    """
    left_eye, right_eye, nose = kps[0], kps[1], kps[2]
    eye_vec = right_eye - left_eye
    eye_dist = float(np.linalg.norm(eye_vec))
    if eye_dist < 1.0:
        return 0.0
    axis = eye_vec / eye_dist
    eye_mid = 0.5 * (left_eye + right_eye)
    proj = float(np.dot(nose - eye_mid, axis))
    return float(np.clip(proj / (0.5 * eye_dist), -1.0, 1.0))


def _estimate_norm(kps: np.ndarray, image_size: int) -> np.ndarray:
    if image_size % 112 == 0:
        ratio = float(image_size) / 112.0
        diff_x = 0.0
    else:
        ratio = float(image_size) / 128.0
        diff_x = 8.0 * ratio
    dst = ARCFACE_DST_112 * ratio
    dst[:, 0] += diff_x
    tform = sktransform.SimilarityTransform.from_estimate(kps, dst)
    return tform.params[0:2, :].astype(np.float32)


def _estimate_ffhq_512(kps: np.ndarray) -> np.ndarray:
    tform = sktransform.SimilarityTransform.from_estimate(kps, FFHQ_DST_512)
    return tform.params[0:2, :].astype(np.float32)


def _extract_source_embedding(source_bgr: np.ndarray) -> tuple[np.ndarray, float, float] | None:
    """Returns (l2_normalized_512d_arcface_embedding, yaw_score, quality_weight)."""
    face = _detect_primary_face(source_bgr)
    if face is None:
        return None
    bbox, kps, score = face
    M = _estimate_norm(kps, 112)
    crop = cv2.warpAffine(source_bgr, M, (112, 112), borderValue=0.0)
    blob = cv2.dnn.blobFromImage(crop, 1.0 / 127.5, (112, 112), (127.5, 127.5, 127.5), swapRB=True)
    sess = _rec_session()
    emb = sess.run(None, {sess.get_inputs()[0].name: blob})[0].reshape(-1).astype(np.float32)
    emb /= max(float(np.linalg.norm(emb)), 1e-6)
    yaw = _estimate_yaw_score(kps)
    bw = max(1.0, float(bbox[2] - bbox[0]))
    bh = max(1.0, float(bbox[3] - bbox[1]))
    size_q = min(1.0, float(np.sqrt(bw * bh)) / 96.0)
    quality = float(np.clip(score, 0.40, 1.0)) * max(0.35, size_q)
    return emb, yaw, quality


def _fuse_source_embedding(
    source_entries: list[tuple[np.ndarray, float, float]],
    target_yaw: float,
    yaw_weight_strength: float = 0.65,
) -> np.ndarray:
    """Fuse 1..5 normalized ArcFace embeddings via yaw-aware L2-normalized centroid fusion."""
    yaw_weight_strength = float(np.clip(yaw_weight_strength, 0.0, 1.0))
    if len(source_entries) == 1:
        fused_emb = source_entries[0][0]
    else:
        acc = np.zeros_like(source_entries[0][0], dtype=np.float32)
        for idx, (emb, src_yaw, quality) in enumerate(source_entries):
            # Gaussian angular affinity; sigma=0.45 sharply favors matching 3/4 or frontal pose
            yaw_diff = target_yaw - src_yaw
            yaw_sim = float(np.exp(-((yaw_diff / 0.45) ** 2)))
            # Keep 0.25 consensus floor so all reference angles stabilize the 3D identity centroid
            angle_factor = (1.0 - yaw_weight_strength) + yaw_weight_strength * (0.25 + 0.75 * yaw_sim)
            # Subtle 1.08 anchor boost for primary slot 0 so user's main portrait stays centered
            anchor_boost = 1.08 if idx == 0 else 1.0
            weight = quality * angle_factor * anchor_boost
            acc += emb * float(weight)
        fused_emb = acc / max(float(np.linalg.norm(acc)), 1e-6)
    return fused_emb.reshape(1, -1).astype(np.float32)


def _to_inswapper_latent(fused_emb: np.ndarray) -> np.ndarray:
    latent = fused_emb @ _emap()
    latent /= max(float(np.linalg.norm(latent)), 1e-6)
    return latent.astype(np.float32)


@lru_cache(maxsize=8)
def _soft_elliptical_mask(size: int, rx: float = 0.38, ry: float = 0.44, cy: float = 0.50, feather: float = 0.16) -> np.ndarray:
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    nx = (xx / (size - 1) - 0.5) / rx
    ny = (yy / (size - 1) - cy) / ry
    dist = np.sqrt(nx * nx + ny * ny)
    mask = np.clip((1.0 - dist) / feather, 0.0, 1.0)
    mask = mask * mask * (3.0 - 2.0 * mask)
    return mask.astype(np.float32)


def _match_scene_lighting_lab(swapped_bgr: np.ndarray, orig_bgr: np.ndarray, mask: np.ndarray, amount: float = 0.35) -> np.ndarray:
    """Preserves both global color temperature AND directional shadows (e.g. chiaroscuro half-lit faces)."""
    if amount <= 0.0:
        return swapped_bgr
    interior = mask > 0.55
    if interior.sum() < 64:
        return swapped_bgr
    lab_src = cv2.cvtColor(swapped_bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    lab_ref = cv2.cvtColor(orig_bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    out = lab_src.copy()

    # Spatial low-frequency luminance transfer: preserves directional window shadows & rim lights cleanly!
    h_img = swapped_bgr.shape[0]
    sigma = max(8.0, h_img / 10.0)
    low_src_l = cv2.GaussianBlur(lab_src[:, :, 0], (0, 0), sigmaX=sigma, sigmaY=sigma)
    low_ref_l = cv2.GaussianBlur(lab_ref[:, :, 0], (0, 0), sigmaX=sigma, sigmaY=sigma)
    # Transfer low-frequency shading delta gently while keeping swapped high-frequency facial micro-shading
    l_spatial = np.clip(lab_src[:, :, 0] + (low_ref_l - low_src_l) * min(0.68, amount * 1.65), 0.0, 255.0)
    out[:, :, 0] = l_spatial

    # Gentle chroma (A, B) color-temperature match so golden hour / tungsten / neon hues look cohesive
    for ch, factor in ((1, amount * 0.65), (2, amount * 0.65)):
        s_vals = lab_src[:, :, ch][interior]
        r_vals = lab_ref[:, :, ch][interior]
        s_mean, s_std = float(s_vals.mean()), max(float(s_vals.std()), 1e-3)
        r_mean, r_std = float(r_vals.mean()), max(float(r_vals.std()), 1e-3)
        ratio = np.clip(r_std / s_std, 0.78, 1.28)
        matched = (lab_src[:, :, ch] - s_mean) * ratio + r_mean
        out[:, :, ch] = lab_src[:, :, ch] * (1.0 - factor) + matched * factor
    out = np.clip(out, 0, 255).astype(np.uint8)
    return cv2.cvtColor(out, cv2.COLOR_LAB2BGR)


def _run_gfpgan_512(bgr_512: np.ndarray, sess: ort.InferenceSession) -> np.ndarray:
    rgb = cv2.cvtColor(bgr_512, cv2.COLOR_BGR2RGB).astype(np.float32)
    inp = ((rgb / 255.0 - 0.5) / 0.5).transpose(2, 0, 1)[None, :, :, :].astype(np.float32)
    out = sess.run(None, {sess.get_inputs()[0].name: inp})[0][0]
    out = np.clip((out.transpose(1, 2, 0) * 0.5 + 0.5), 0.0, 1.0)
    out_u8 = (out * 255.0).round().astype(np.uint8)
    return cv2.cvtColor(out_u8, cv2.COLOR_RGB2BGR)


def _extract_source_embedding_cached(src_rgb: np.ndarray) -> tuple[np.ndarray, float, float] | None:
    key = (src_rgb.shape, hashlib.blake2b(src_rgb.tobytes(), digest_size=16).digest())
    with _SOURCE_EMB_LOCK:
        if key in _SOURCE_EMB_CACHE:
            _SOURCE_EMB_CACHE.move_to_end(key)
            return _SOURCE_EMB_CACHE[key]
    src_bgr = cv2.cvtColor(src_rgb, cv2.COLOR_RGB2BGR)
    entry = _extract_source_embedding(src_bgr)
    with _SOURCE_EMB_LOCK:
        _SOURCE_EMB_CACHE[key] = entry
        _SOURCE_EMB_CACHE.move_to_end(key)
        while len(_SOURCE_EMB_CACHE) > _SOURCE_EMB_CACHE_MAX:
            _SOURCE_EMB_CACHE.popitem(last=False)
    return entry


def _swap_one_face_in_place(
    working_bgr: np.ndarray,
    target_kps: np.ndarray,
    source_entries: list[tuple[np.ndarray, float, float]],
    strength: float,
    restore_strength: float,
    yaw_weight_strength: float,
    hyper_sess,
    inswap_sess_box: list,
    gfpgan_sess_box: list,
) -> tuple[np.ndarray, bool]:
    h, w = working_bgr.shape[:2]
    target_yaw = _estimate_yaw_score(target_kps)
    fused_emb = _fuse_source_embedding(source_entries, target_yaw=target_yaw, yaw_weight_strength=yaw_weight_strength)
    min_yaw_gap = min(abs(target_yaw - entry[1]) for entry in source_entries)
    face_strength = strength * (0.78 if min_yaw_gap > 0.60 else (0.88 if min_yaw_gap > 0.42 else 1.0))

    face_used_hyper = False
    swapped_full = working_bgr
    if hyper_sess is not None:
        try:
            M256 = _estimate_norm(target_kps, 256)
            crop256 = cv2.warpAffine(working_bgr, M256, (256, 256), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REFLECT)
            crop256_rgb = cv2.cvtColor(crop256, cv2.COLOR_BGR2RGB).astype(np.float32)
            blob256 = ((crop256_rgb / 255.0 - 0.5) / 0.5).transpose(2, 0, 1)[None, :, :, :].astype(np.float32)
            hyper_outs = hyper_sess.run(None, {"source": fused_emb, "target": blob256})
            out256 = np.clip(hyper_outs[0][0].transpose(1, 2, 0) * 0.5 + 0.5, 0.0, 1.0)
            swapped_bgr_256 = cv2.cvtColor((out256 * 255.0).round().astype(np.uint8), cv2.COLOR_RGB2BGR)

            guard256 = _soft_elliptical_mask(256, rx=0.41, ry=0.46, cy=0.51, feather=0.20)
            if len(hyper_outs) > 1 and hyper_outs[1] is not None:
                raw_m = np.squeeze(hyper_outs[1]).astype(np.float32)
                if raw_m.ndim == 2 and raw_m.shape == (256, 256):
                    if float(raw_m.min()) < -0.1:
                        raw_m = raw_m * 0.5 + 0.5
                    raw_m = np.clip(raw_m, 0.0, 1.0)
                    raw_m = cv2.GaussianBlur(raw_m, (9, 9), 2.5)
                    mask256 = np.clip(guard256 * np.maximum(raw_m, 0.15 * guard256), 0.0, 1.0)
                else:
                    mask256 = guard256
            else:
                mask256 = guard256

            swapped_bgr_256 = _match_scene_lighting_lab(swapped_bgr_256, crop256, mask256, amount=0.42)

            # Expression-Preserving Micro-Detail Fusion:
            # Retain natural brow furrows, eye catchlights, smile creases, and dynamic mouth movement
            # from the generated scene so face-locked characters act expressively instead of looking stiff.
            crop_f = crop256.astype(np.float32)
            swap_f = swapped_bgr_256.astype(np.float32)
            crop_hi = crop_f - cv2.GaussianBlur(crop_f, (0, 0), sigmaX=3.2, sigmaY=3.2)
            swap_hi = swap_f - cv2.GaussianBlur(swap_f, (0, 0), sigmaX=3.2, sigmaY=3.2)
            swapped_bgr_256 = np.clip(swap_f + (crop_hi - swap_hi) * (0.24 * mask256[:, :, None]), 0.0, 255.0).astype(np.uint8)

            # Slightly relax overwrite right over dynamic lips/mouth center in canonical 256 ArcFace space
            # so smiles, open-mouth shock, and intense focus tension stay organic while keeping 100% identity.
            yy, xx = np.mgrid[0:256, 0:256].astype(np.float32)
            mouth_dist = ((xx - 128.0) / 34.0) ** 2 + ((yy - 178.0) / 20.0) ** 2
            mouth_relax = np.clip(1.0 - np.exp(-mouth_dist * 1.4), 0.76, 1.0).astype(np.float32)
            mask256_expr = mask256 * mouth_relax

            IM256 = cv2.invertAffineTransform(M256)
            warped_swap = cv2.warpAffine(swapped_bgr_256, IM256, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
            warped_mask = cv2.warpAffine(mask256_expr, IM256, (w, h), flags=cv2.INTER_LINEAR, borderValue=0.0)
            warped_mask_3 = (warped_mask * face_strength)[:, :, None]
            swapped_full = np.clip(
                working_bgr.astype(np.float32) * (1.0 - warped_mask_3) + warped_swap.astype(np.float32) * warped_mask_3,
                0,
                255,
            ).astype(np.uint8)
            face_used_hyper = True
        except Exception as exc:
            print(f"[CcaFaceIdentitySwap] HyperSwap 256 fallback to InSwapper 128: {exc}")

    if not face_used_hyper:
        if inswap_sess_box[0] is None:
            inswap_sess_box[0] = _get_inswapper_session()
        latent = _to_inswapper_latent(fused_emb)
        M128 = _estimate_norm(target_kps, 128)
        crop128 = cv2.warpAffine(working_bgr, M128, (128, 128), borderValue=0.0)
        blob128 = cv2.dnn.blobFromImage(crop128, 1.0 / 255.0, (128, 128), (0.0, 0.0, 0.0), swapRB=True)
        swapped_out = inswap_sess_box[0].run(None, {"target": blob128, "source": latent})[0][0]
        swapped_rgb = np.clip(swapped_out.transpose(1, 2, 0), 0.0, 1.0)
        swapped_bgr_128 = cv2.cvtColor((swapped_rgb * 255.0).round().astype(np.uint8), cv2.COLOR_RGB2BGR)

        mask128 = _soft_elliptical_mask(128, rx=0.39, ry=0.45, cy=0.52, feather=0.22)
        swapped_bgr_128 = _match_scene_lighting_lab(swapped_bgr_128, crop128, mask128, amount=0.36)

        IM128 = cv2.invertAffineTransform(M128)
        warped_swap = cv2.warpAffine(swapped_bgr_128, IM128, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
        warped_mask = cv2.warpAffine(mask128, IM128, (w, h), flags=cv2.INTER_LINEAR, borderValue=0.0)
        warped_mask_3 = (warped_mask * face_strength)[:, :, None]
        swapped_full = np.clip(
            working_bgr.astype(np.float32) * (1.0 - warped_mask_3) + warped_swap.astype(np.float32) * warped_mask_3,
            0,
            255,
        ).astype(np.uint8)

    eff_restore = min(restore_strength, 0.30) if face_used_hyper else restore_strength
    if eff_restore > 0.02:
        try:
            if gfpgan_sess_box[0] is None:
                gfpgan_sess_box[0] = _get_gfpgan_session()
            M512 = _estimate_ffhq_512(target_kps)
            crop512_swapped = cv2.warpAffine(swapped_full, M512, (512, 512), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REFLECT)
            crop512_orig = cv2.warpAffine(working_bgr, M512, (512, 512), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REFLECT)
            restored512 = _run_gfpgan_512(crop512_swapped, gfpgan_sess_box[0])
            mask512 = _soft_elliptical_mask(512, rx=0.34, ry=0.40, cy=0.56, feather=0.20)
            restored512 = _match_scene_lighting_lab(restored512, crop512_orig, mask512, amount=0.25)
            blended512 = np.clip(
                crop512_swapped.astype(np.float32) * (1.0 - eff_restore) + restored512.astype(np.float32) * eff_restore,
                0,
                255,
            ).astype(np.uint8)

            IM512 = cv2.invertAffineTransform(M512)
            warped_restored = cv2.warpAffine(blended512, IM512, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
            warped_rmask = cv2.warpAffine(mask512, IM512, (w, h), flags=cv2.INTER_LINEAR, borderValue=0.0)[:, :, None]
            return np.clip(
                swapped_full.astype(np.float32) * (1.0 - warped_rmask) + warped_restored.astype(np.float32) * warped_rmask,
                0,
                255,
            ).astype(np.uint8), face_used_hyper
        except Exception as exc:
            print(f"[CcaFaceIdentitySwap] GFPGAN skipped cleanly due to host memory pressure: {exc}")
            return swapped_full, face_used_hyper
    return swapped_full, face_used_hyper


def swap_and_restore_face(
    target_rgb_u8: np.ndarray,
    source_rgb_u8_or_list: np.ndarray | list[np.ndarray],
    strength: float = 0.90,
    restore_strength: float = 0.40,
    yaw_weight_strength: float = 0.65,
) -> tuple[np.ndarray, str]:
    strength = float(np.clip(strength, 0.0, 1.0))
    restore_strength = float(np.clip(restore_strength, 0.0, 1.0))
    if strength <= 0.01:
        return target_rgb_u8, "bypassed"

    source_list = (
        source_rgb_u8_or_list
        if isinstance(source_rgb_u8_or_list, list)
        else [source_rgb_u8_or_list]
    )

    source_entries: list[tuple[np.ndarray, float, float]] = []
    for src_rgb in source_list[:5]:
        entry = _extract_source_embedding_cached(src_rgb)
        if entry is not None:
            source_entries.append(entry)

    if not source_entries:
        return target_rgb_u8, "no_source_face"

    target_bgr = cv2.cvtColor(target_rgb_u8, cv2.COLOR_RGB2BGR)
    detected_faces = _detect_all_faces(target_bgr)
    if not detected_faces:
        return target_rgb_u8, "no_target_face"

    is_expression_grid = False
    if len(detected_faces) >= 4:
        areas = [max(1.0, float((f[0][2] - f[0][0]) * (f[0][3] - f[0][1]))) for f in detected_faces[:4]]
        if (min(areas) / max(areas)) >= 0.45:
            is_expression_grid = True
    faces_to_swap = detected_faces[:12] if is_expression_grid else [detected_faces[0]]

    _begin_heavy_use()
    inswap_box = [None]
    gfpgan_box = [None]
    used_hyperswap = False
    working_bgr = target_bgr.copy()
    try:
        hyper_sess = _get_hyperswap_session()
        for _, target_kps, _ in faces_to_swap:
            working_bgr, face_hyper = _swap_one_face_in_place(
                working_bgr, target_kps, source_entries, strength, restore_strength, yaw_weight_strength,
                hyper_sess, inswap_box, gfpgan_box,
            )
            used_hyperswap = used_hyperswap or face_hyper
    finally:
        _end_heavy_use()

    tag = "swapped_256" if used_hyperswap else "swapped"
    if len(faces_to_swap) > 1:
        status = f"{tag}_grid_{len(faces_to_swap)}faces"
    else:
        status = tag if len(source_entries) == 1 else f"{tag}_multi_{len(source_entries)}refs"
    return cv2.cvtColor(working_bgr, cv2.COLOR_BGR2RGB), status


def swap_dual_faces_left_right(
    target_rgb_u8: np.ndarray,
    left_sources: list[np.ndarray],
    right_sources: list[np.ndarray],
    left_strength: float = 0.90,
    left_restore: float = 0.40,
    right_strength: float = 0.90,
    right_restore: float = 0.40,
    yaw_weight_strength: float = 0.65,
) -> tuple[np.ndarray, str]:
    """Locks two distinct identities onto the two most prominent faces sorted strictly Left-to-Right."""
    left_entries = [e for src in left_sources[:5] if (e := _extract_source_embedding_cached(src)) is not None]
    right_entries = [e for src in right_sources[:5] if (e := _extract_source_embedding_cached(src)) is not None]
    if not left_entries and not right_entries:
        return target_rgb_u8, "no_source_face"

    target_bgr = cv2.cvtColor(target_rgb_u8, cv2.COLOR_RGB2BGR)
    detected_faces = _detect_all_faces(target_bgr)
    if not detected_faces:
        return target_rgb_u8, "no_target_face"

    # Keep top 2 prominent foreground faces (filtering tiny distant background pedestrians <25% area)
    max_area = max(1.0, float((detected_faces[0][0][2] - detected_faces[0][0][0]) * (detected_faces[0][0][3] - detected_faces[0][0][1])))
    foreground = [
        f for f in detected_faces
        if float((f[0][2] - f[0][0]) * (f[0][3] - f[0][1])) >= 0.25 * max_area
    ][:2]
    # Sort strictly Left -> Right by horizontal face center X
    foreground.sort(key=lambda f: 0.5 * float(f[0][0] + f[0][2]))

    _begin_heavy_use()
    inswap_box = [None]
    gfpgan_box = [None]
    used_hyperswap = False
    working_bgr = target_bgr.copy()
    swapped_count = 0
    try:
        hyper_sess = _get_hyperswap_session()
        if len(foreground) >= 1 and left_entries and left_strength > 0.01:
            working_bgr, fh = _swap_one_face_in_place(
                working_bgr, foreground[0][1], left_entries,
                float(np.clip(left_strength, 0.0, 1.0)), float(np.clip(left_restore, 0.0, 1.0)),
                yaw_weight_strength, hyper_sess, inswap_box, gfpgan_box,
            )
            used_hyperswap = used_hyperswap or fh
            swapped_count += 1

        if len(foreground) >= 2 and right_entries and right_strength > 0.01:
            working_bgr, fh = _swap_one_face_in_place(
                working_bgr, foreground[1][1], right_entries,
                float(np.clip(right_strength, 0.0, 1.0)), float(np.clip(right_restore, 0.0, 1.0)),
                yaw_weight_strength, hyper_sess, inswap_box, gfpgan_box,
            )
            used_hyperswap = used_hyperswap or fh
            swapped_count += 1
    finally:
        _end_heavy_use()

    tag = "dual_256" if used_hyperswap else "dual_128"
    return cv2.cvtColor(working_bgr, cv2.COLOR_BGR2RGB), f"{tag}_{swapped_count}faces"


def _tensor_frames_to_rgb_list(tensor) -> list[np.ndarray]:
    if tensor is None:
        return []
    frames: list[np.ndarray] = []
    for i in range(tensor.shape[0]):
        arr = (tensor[i].detach().cpu().clamp(0, 1).numpy() * 255.0).round().astype(np.uint8)
        frames.append(arr)
    return frames


class CcaFaceIdentitySwap:
    """Locks character facial identity from 1..5 multi-angle source faces onto target_image (0 VRAM)."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "target_image": ("IMAGE",),
                "source_face": ("IMAGE",),
                "strength": ("FLOAT", {"default": 0.90, "min": 0.0, "max": 1.0, "step": 0.05}),
                "restore_strength": ("FLOAT", {"default": 0.40, "min": 0.0, "max": 1.0, "step": 0.05}),
            },
            "optional": {
                "source_face_2": ("IMAGE",),
                "source_face_3": ("IMAGE",),
                "source_face_4": ("IMAGE",),
                "source_face_5": ("IMAGE",),
                "yaw_weight_strength": ("FLOAT", {"default": 0.65, "min": 0.0, "max": 1.0, "step": 0.05}),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "apply"
    CATEGORY = "CharacterConceptAI"

    def apply(
        self,
        target_image,
        source_face,
        strength=0.90,
        restore_strength=0.40,
        source_face_2=None,
        source_face_3=None,
        source_face_4=None,
        source_face_5=None,
        yaw_weight_strength=0.65,
    ):
        source_rgb_list: list[np.ndarray] = []
        for tensor in (source_face, source_face_2, source_face_3, source_face_4, source_face_5):
            source_rgb_list.extend(_tensor_frames_to_rgb_list(tensor))
            if len(source_rgb_list) >= 5:
                break
        source_rgb_list = source_rgb_list[:5]

        out_frames = []
        statuses = []
        for idx in range(target_image.shape[0]):
            tgt_rgb = (target_image[idx].detach().cpu().clamp(0, 1).numpy() * 255.0).round().astype(np.uint8)
            swapped, status = swap_and_restore_face(
                tgt_rgb,
                source_rgb_list,
                strength=strength,
                restore_strength=restore_strength,
                yaw_weight_strength=yaw_weight_strength,
            )
            out_frames.append(torch.from_numpy(swapped.astype(np.float32) / 255.0))
            statuses.append(status)
        batch = torch.stack(out_frames, dim=0).to(target_image.device, dtype=target_image.dtype)
        return {"ui": {"cca_face_identity_status": statuses}, "result": (batch,)}


class CcaDualFaceIdentitySwap:
    """Locks two distinct character identities (Left & Right) in the same frame (Local 0-VRAM)."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "target_image": ("IMAGE",),
                "left_face": ("IMAGE",),
                "right_face": ("IMAGE",),
                "left_strength": ("FLOAT", {"default": 0.90, "min": 0.0, "max": 1.0, "step": 0.05}),
                "left_restore": ("FLOAT", {"default": 0.40, "min": 0.0, "max": 1.0, "step": 0.05}),
                "right_strength": ("FLOAT", {"default": 0.90, "min": 0.0, "max": 1.0, "step": 0.05}),
                "right_restore": ("FLOAT", {"default": 0.40, "min": 0.0, "max": 1.0, "step": 0.05}),
            },
            "optional": {
                "left_face_2": ("IMAGE",),
                "left_face_3": ("IMAGE",),
                "right_face_2": ("IMAGE",),
                "right_face_3": ("IMAGE",),
                "yaw_weight_strength": ("FLOAT", {"default": 0.65, "min": 0.0, "max": 1.0, "step": 0.05}),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "apply"
    CATEGORY = "CharacterConceptAI"

    def apply(
        self,
        target_image,
        left_face,
        right_face,
        left_strength=0.90,
        left_restore=0.40,
        right_strength=0.90,
        right_restore=0.40,
        left_face_2=None,
        left_face_3=None,
        right_face_2=None,
        right_face_3=None,
        yaw_weight_strength=0.65,
    ):
        left_list: list[np.ndarray] = []
        for t in (left_face, left_face_2, left_face_3):
            left_list.extend(_tensor_frames_to_rgb_list(t))
        right_list: list[np.ndarray] = []
        for t in (right_face, right_face_2, right_face_3):
            right_list.extend(_tensor_frames_to_rgb_list(t))

        out_frames = []
        statuses = []
        for idx in range(target_image.shape[0]):
            tgt_rgb = (target_image[idx].detach().cpu().clamp(0, 1).numpy() * 255.0).round().astype(np.uint8)
            swapped, status = swap_dual_faces_left_right(
                tgt_rgb,
                left_list[:5],
                right_list[:5],
                left_strength=left_strength,
                left_restore=left_restore,
                right_strength=right_strength,
                right_restore=right_restore,
                yaw_weight_strength=yaw_weight_strength,
            )
            out_frames.append(torch.from_numpy(swapped.astype(np.float32) / 255.0))
            statuses.append(status)
        batch = torch.stack(out_frames, dim=0).to(target_image.device, dtype=target_image.dtype)
        return {"ui": {"cca_face_identity_status": statuses}, "result": (batch,)}


NODE_CLASS_MAPPINGS = {
    "CcaFaceIdentitySwap": CcaFaceIdentitySwap,
    "CcaDualFaceIdentitySwap": CcaDualFaceIdentitySwap,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "CcaFaceIdentitySwap": "CCA Face Identity Lock Multi-Angle (Local 0-VRAM)",
    "CcaDualFaceIdentitySwap": "CCA Dual-Face Couple Identity Lock (Left/Right 0-VRAM)",
}
