"""Refine the most prominent face while preserving the rest of the picture."""

from functools import lru_cache
from pathlib import Path
import re

import cv2
import numpy as np
import torch
from torch.nn import functional as F


ROOT = Path(__file__).resolve().parent
DETECT_SIZE = 640
CROP_SIZE = 512
ANIME = re.compile(r"\b(anime|manga|cel[ -]shaded|cartoon|toon)\b", re.IGNORECASE)
PHOTO = re.compile(r"\b(photorealistic|photograph|photography|realistic photo)\b", re.IGNORECASE)


@lru_cache(maxsize=1)
def _photo_detector():
    return cv2.FaceDetectorYN.create(str(ROOT / "face_detection_yunet_2023mar.onnx"), "",
                                   (DETECT_SIZE, DETECT_SIZE), .78, .3, 100)


@lru_cache(maxsize=1)
def _anime_detector():
    return cv2.dnn.readNetFromONNX(str(ROOT / "anime_face_v1.4_n.onnx"))


def _detect(image, anime, photo_threshold=.78):
    height, width = image.shape[:2]
    scale = DETECT_SIZE / max(height, width)
    rgb = (image.detach().cpu().clamp(0, 1).numpy() * 255).astype(np.uint8)
    bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    thumb = cv2.resize(bgr, (round(width * scale), round(height * scale)))
    canvas = np.full((DETECT_SIZE, DETECT_SIZE, 3), 114, dtype=np.uint8)
    canvas[:thumb.shape[0], :thumb.shape[1]] = thumb
    if anime:
        model = _anime_detector()
        model.setInput(cv2.dnn.blobFromImage(canvas, 1 / 255., swapRB=True))
        xywh = model.forward()[0].T
        hits = xywh[xywh[:, 4] > .55].copy()
        hits[:, :2] -= hits[:, 2:4] / 2
    else:
        detector = _photo_detector()
        detector.setScoreThreshold(photo_threshold)
        _, hits = detector.detect(canvas)
        if hits is None:
            return None

    viable = []
    for hit in hits:
        x, y, w, h = hit[:4] / scale
        if not (w >= 24 and h >= 24 and x >= 0 and y >= 0 and x + w <= width + 1 and y + h <= height + 1):
            continue
        landmarks = None if anime else hit[4:14].reshape(5, 2) / scale
        viable.append(((x, y, w, h), landmarks, float(hit[-1])))
    return max(viable, key=lambda hit: hit[0][2] * hit[0][3] * hit[2], default=None)


def _resize(image, height, width):
    return F.interpolate(image.movedim(-1, 1), size=(height, width), mode="bicubic",
                         align_corners=False, antialias=True).movedim(1, -1).clamp(0, 1)


def _matches_geometry(original, candidate):
    (x, y, w, h), landmarks, _ = original
    (rx, ry, rw, rh), refined, _ = candidate
    overlap_w = max(0., min(x + w, rx + rw) - max(x, rx))
    overlap_h = max(0., min(y + h, ry + rh) - max(y, ry))
    overlap = overlap_w * overlap_h
    if overlap / (w * h + rw * rh - overlap) < .73:
        return False
    if landmarks is not None:
        distances = np.linalg.norm(landmarks - refined, axis=1)
        return distances.mean() <= .09 * w and distances.max() <= .18 * w
    return True


def _detail_score(image, mask):
    luminance = cv2.cvtColor(image.detach().cpu().numpy().astype(np.float32), cv2.COLOR_RGB2GRAY)
    edges = np.hypot(cv2.Sobel(luminance, cv2.CV_32F, 1, 0), cv2.Sobel(luminance, cv2.CV_32F, 0, 1))
    face_edges = edges[mask]
    return float(np.quantile(face_edges, .85))


def _local_sharpen(patch):
    rgb = patch[0].detach().cpu().numpy()
    soft = cv2.GaussianBlur(rgb, (0, 0), .85)
    detail = np.clip(rgb - soft, -.045, .045)
    return torch.from_numpy(np.clip(rgb + .65 * detail, 0., 1.)).unsqueeze(0).to(patch.device)


def _align_face(refined, original_landmarks, refined_landmarks, face_width):
    if original_landmarks is None:
        return refined
    dx, dy = np.clip((original_landmarks - refined_landmarks).mean(axis=0), -.03 * face_width, .03 * face_width)
    if abs(dx) + abs(dy) < .25:
        return refined
    matrix = np.asarray([[1., 0., dx], [0., 1., dy]], dtype=np.float32)
    rgb = refined[0].detach().cpu().numpy()
    aligned = cv2.warpAffine(rgb, matrix, (CROP_SIZE, CROP_SIZE), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REFLECT)
    return torch.from_numpy(aligned).unsqueeze(0).to(refined.device).clamp(0, 1)


class CcaFaceCrop:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"image": ("IMAGE",), "prompt": ("STRING", {"forceInput": False})}}

    RETURN_TYPES = ("IMAGE", "CCA_FACE_REGION", "STRING", "FLOAT")
    FUNCTION = "crop"
    CATEGORY = "CharacterConceptAI"

    def crop(self, image, prompt):
        if image.shape[0] != 1:
            raise ValueError("Face refinement expects one image.")
        explicit_photo = bool(PHOTO.search(prompt))
        anime = bool(ANIME.search(prompt)) and not explicit_photo
        detection = _detect(image[0], anime)
        if detection is None and not anime and not explicit_photo:
            detection = _detect(image[0], True)
            anime = detection is not None
        if detection is None:
            return (image, {}, "", .30)

        (x, y, width, height), _, _ = detection
        side = min(image.shape[1], image.shape[2], round(max(width, height) * 1.65))
        left = max(0, min(image.shape[2] - side, round(x + width / 2 - side / 2)))
        top = max(0, min(image.shape[1] - side, round(y + height / 2 - side / 2)))
        crop = _resize(image[:, top:top + side, left:left + side], CROP_SIZE, CROP_SIZE)
        factor = CROP_SIZE / side
        (x, y, w, h), landmarks, confidence = detection
        baseline_box = ((x - left) * factor, (y - top) * factor, w * factor, h * factor)
        fx, fy, fw, fh = baseline_box
        if fx < 0 or fy < 0 or fx + fw > CROP_SIZE or fy + fh > CROP_SIZE:
            return (image, {}, "", .30)
        if landmarks is not None:
            landmarks = (landmarks - np.array([left, top], dtype=np.float32)) * factor
        baseline = (baseline_box, landmarks, confidence)
        face = crop[:, max(0, int(fy)):min(CROP_SIZE, round(fy + fh)),
                    max(0, int(fx)):min(CROP_SIZE, round(fx + fw))]
        axis = np.linspace(-1., 1., 96)
        face_interior = axis[:, None] ** 2 + axis[None, :] ** 2 < .64
        detail = _detail_score(_resize(face, 96, 96)[0], face_interior)
        denoise = .46 if detail < .26 else .30
        style = "Anime illustration with consistent linework" if anime else "Preserve the original rendering style and natural facial details"
        prompt = (f"{style}. Close-up of the same face. Keep the same expression, eye color, "
                  "gaze, hair and lighting. Clearly defined eyes, eyelids, nose and lips.")
        region = {"left": left, "top": top, "side": side, "baseline": baseline, "anime": anime, "crop": crop}
        return (crop, region, prompt, denoise)


class CcaFaceComposite:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {"image": ("IMAGE",), "region": ("CCA_FACE_REGION",),
                             "refined": ("IMAGE", {"lazy": True})}}

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "compose"
    CATEGORY = "CharacterConceptAI"

    def check_lazy_status(self, image, region, refined):
        return ["refined"] if region and refined is None else []

    def compose(self, image, region, refined=None):
        if not region:
            return self.result(image, "no_face")
        if refined is None or not torch.isfinite(refined).all():
            raise ValueError("Face refinement did not return a valid image.")

        candidate = _detect(refined[0], region["anime"], photo_threshold=.68)
        baseline = region["baseline"]
        if candidate is None or not _matches_geometry(baseline, candidate):
            return self.result(image, "kept_source")

        x, y, w, h = baseline[0]
        left, top, side = (region[k] * 2 for k in ("left", "top", "side"))
        reference = region["crop"].to(refined.device)
        l, t, r, b = int(x), int(y), int(x + w), int(y + h)
        if (reference[:, t:b, l:r] - refined[:, t:b, l:r]).abs().mean().item() > .14:
            return self.result(image, "kept_source")

        refined = _align_face(refined, baseline[1], candidate[1], w)
        patch = _local_sharpen(_resize(refined.to(image.device), side, side))
        axis = (torch.arange(side, device=image.device, dtype=torch.float32) + .5) * (CROP_SIZE / side)
        dx = (axis - x - w / 2) / (.66 * w)
        dy = (axis - y - h / 2) / (.68 * h)
        radius = torch.sqrt(dy[:, None].square() + dx[None, :].square())
        blend = ((1. - radius) / .35).clamp(0, 1)
        blend = (blend.square() * (3. - 2. * blend))[None, :, :, None]
        target = image[:, top:top + side, left:left + side]
        merged = torch.lerp(target, patch.to(target.dtype), blend.to(target.dtype))
        face_interior = (radius < .70).cpu().numpy()
        if _detail_score(merged[0], face_interior) < 1.04 * _detail_score(target[0], face_interior):
            return self.result(image, "kept_source")

        result = image.clone()
        result[:, top:top + side, left:left + side] = merged
        return self.result(result, "refined")

    @staticmethod
    def result(image, status):
        return {"ui": {"cca_face_status": [status]}, "result": (image,)}


NODE_CLASS_MAPPINGS = {
    "CcaFaceCrop": CcaFaceCrop,
    "CcaFaceComposite": CcaFaceComposite,
}
