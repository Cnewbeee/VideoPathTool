@echo off
rem Open labelme on a dataset split. Boxes are pre-drawn; only fix what is wrong.
for %%i in ("%~dp0..") do set ROOT=%%~fi
set LABELME=%ROOT%\video\.venv-labelme\Scripts\labelme.exe
set PY=%ROOT%\video\.venv-yolo\Scripts\python.exe
if not exist "%LABELME%" (
  echo labelme not found: %LABELME%
  echo install it with: video\.venv-labelme\Scripts\python.exe -m pip install --index-url https://pypi.org/simple labelme
  pause
  exit /b 1
)

echo Opening VAL split: %ROOT%\video\yolo_ds\images\val
echo.
echo   box is good      -^> press D for next
echo   box is wrong     -^> drag a corner, or delete and redraw
echo   missing box      -^> draw a rectangle, label must be: glider
echo   should have none -^> delete the box (empty = negative sample)
echo   press Ctrl+S to save, then run label-apply.bat
echo.
start "" "%LABELME%" --no-sort-labels --labels glider "%ROOT%\video\yolo_ds\images\val"