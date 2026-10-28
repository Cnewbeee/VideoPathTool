@echo off
rem Write edited labelme json back to YOLO txt for the new-region staging set.
for %%i in ("%~dp0..") do set ROOT=%%~fi
set PY=%ROOT%\video\.venv-yolo\Scripts\python.exe
if not exist "%PY%" (
  echo python not found: %PY%
  pause
  exit /b 1
)
echo Writing json back to YOLO txt ...
"%PY%" "%ROOT%\training\_labelme2yolo.py" --root "%ROOT%\video\yolo_new\inazuma" --split train
echo.
echo Next: merge into the dataset with
echo   python training/_merge_new.py
pause
