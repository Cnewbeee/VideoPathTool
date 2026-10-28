@echo off
rem Open labelme on a dataset split. Boxes are pre-drawn; only fix what is wrong.
for %%i in ("%~dp0..") do set ROOT=%%~fi
set LABELME=%ROOT%\video\.venv-labelme\Scripts\labelme.exe
set PY=%ROOT%\video\.venv-yolo\Scripts\python.exe
cd /d "%ROOT%"
echo === preview (nothing is written) ===
"%PY%" "%~dp0_labelme2yolo.py" --dry-run
echo.
choice /C YN /M "Write these changes back"
if errorlevel 2 goto cancelled

echo.
echo === writing ===
"%PY%" "%~dp0_labelme2yolo.py"
echo.
echo done. old labels backed up in video\yolo_ds\labels_backup\
pause
exit /b 0

:cancelled
echo cancelled, nothing changed.
pause