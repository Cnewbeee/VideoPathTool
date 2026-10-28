@echo off
for %%i in ("%~dp0..") do set ROOT=%%~fi
set PY=%ROOT%\video\.venv-yolo\Scripts\python.exe
set LABELME=%ROOT%\video\.venv-labelme\Scripts\labelme.exe
if not exist "%LABELME%" (
  echo 找不到 labelme: %LABELME%
  pause
  exit /b 1
)

echo 打开训练集: %ROOT%\video\yolo_ds\images\train
echo.
echo   框是对的   -^> 按 D 下一张
echo   框画错了   -^> 拖角点修正，或删掉重画
echo   该有框没有 -^> 画矩形，标签填 glider
echo   不该有框   -^> 删掉框（存成空 = 负样本）
echo   改完按 Ctrl+S 保存，全部看完再运行 label-apply.bat 写回
echo.
start "" "%LABELME%" --no-sort-labels --labels glider "%ROOT%\video\yolo_ds\images\train"