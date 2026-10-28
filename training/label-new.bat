@echo off
rem Open labelme on the NEW REGION staging set. It is NOT part of training yet.
for %%i in ("%~dp0..") do set ROOT=%%~fi
set LABELME=%ROOT%\video\.venv-labelme\Scripts\labelme.exe
if not exist "%LABELME%" (
  echo labelme not found: %LABELME%
  pause
  exit /b 1
)

echo Opening NEW REGION set: %ROOT%\video\yolo_new\inazuma\images\train
echo.
echo   293 images: 39 auto-labelled positives + 254 negatives.
echo   The 190 "mm_*" ones are already confirmed minimap false positives,
echo   you can skip them (press D repeatedly) or review briefly.
echo.
echo   box is good      -^> press D for next
echo   box is wrong     -^> drag a corner, or delete and redraw
echo   missing box      -^> draw a rectangle, label must be: glider
echo   should have none -^> delete the box (empty = negative sample)
echo   press Ctrl+S to save, then run label-apply-new.bat
echo.
start "" "%LABELME%" --no-sort-labels --labels glider "%ROOT%\video\yolo_new\inazuma\images\train"
