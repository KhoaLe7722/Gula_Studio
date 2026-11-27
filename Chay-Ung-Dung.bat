@echo off
chcp 65001 >nul
title Gula Studio
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-desktop.ps1" %*
if errorlevel 1 pause
