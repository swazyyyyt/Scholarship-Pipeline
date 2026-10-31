@echo off
rem Windows x64 build (MinGW-w64 gcc on PATH):  native\build.cmd
rem Alternative with MSVC (Developer Command Prompt):
rem   cl /LD /O2 native\scholarship_native.c /Fe:native\lib\win-x64\scholarship_native.dll
if not exist "%~dp0lib\win-x64" mkdir "%~dp0lib\win-x64"
gcc -std=c11 -Wall -Wextra -Werror -O2 -shared -static -static-libgcc -o "%~dp0lib\win-x64\scholarship_native.dll" "%~dp0scholarship_native.c"
if errorlevel 1 exit /b 1
echo built %~dp0lib\win-x64\scholarship_native.dll
