@echo off
chcp 65001 >nul
title ComfyUI - Gula Studio
set "ROOT=%~dp0"
cd /d "%ROOT%ComfyUI"

set "PY_EXE="
if exist "%ROOT%python_embeded\python.exe" set "PY_EXE=%ROOT%python_embeded\python.exe"
if not defined PY_EXE if exist "%ROOT%ComfyUI\venv\Scripts\python.exe" set "PY_EXE=%ROOT%ComfyUI\venv\Scripts\python.exe"
if not defined PY_EXE if exist "%ROOT%ComfyUI\.venv\Scripts\python.exe" set "PY_EXE=%ROOT%ComfyUI\.venv\Scripts\python.exe"
if not defined PY_EXE set "PY_EXE=python"

echo Starting ComfyUI for Gula Studio (6GB+ VRAM Optimized: fp16_accumulation + safe host memory)...
"%PY_EXE%" main.py --listen 127.0.0.1 --port 8188 --windows-standalone-build --use-pytorch-cross-attention --fast fp16_accumulation --reserve-vram 0.8 --disable-pinned-memory --disable-auto-launch
if /i "%~1" neq "--no-pause" pause
