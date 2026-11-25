@echo off
:: 在当前脚本所在目录打开命令提示符（"Open Terminal Here"）
:: 若存在本地 venv 则一并激活（通用脚本模板行为，本项目不使用 Python）
cd /d "%~dp0" || exit /b 1
if exist "%~dp0venv\Scripts\activate.bat" (
    call "%~dp0venv\Scripts\activate.bat"
)
cmd /k
