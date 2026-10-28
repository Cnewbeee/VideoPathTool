@echo off
for %%i in ("%~dp0..") do set ROOT=%%~fi
set PY=%ROOT%\video\.venv-yolo\Scripts\python.exe
set LABELME=%ROOT%\video\.venv-labelme\Scripts\labelme.exe
cd /d "%ROOT%"
echo === 变化预览（不写盘） ===
"%PY%" "%~dp0_labelme2yolo.py" --dry-run
echo.
choice /C YN /M "确认写回"
if errorlevel 2 goto cancelled

echo.
echo === 写回 ===
"%PY%" "%~dp0_labelme2yolo.py"
echo.
echo 完成。旧标签备份在 video\yolo_ds\labels_backup\
pause
exit /b 0

:cancelled
echo 已取消，什么都没改。
pause