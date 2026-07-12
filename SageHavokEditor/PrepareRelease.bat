@echo off
setlocal

:: NOTE: do not name variables OutDir / PublishDir here - MSBuild reads environment
:: variables as properties, and those two would redirect the build's own output.
SET "ProjPath=%~dp0SageHavokEditor.csproj"
SET "PubOut=%~dp0bin\Release\net8.0-windows\win-x64\publish"
SET "DistDir=%~dp0SageHavokEditor_Dist"

echo Publishing single-file, self-contained Release build...
dotnet publish "%ProjPath%" -c Release --nologo
if errorlevel 1 goto :fail

echo Cleaning old distribution folder...
if exist "%DistDir%" rd /s /q "%DistDir%"
mkdir "%DistDir%"

:: Publish is single-file and self-contained, so the exe is the whole app.
:: The .pdb sitting next to it is debug info and is left out on purpose.
copy /Y "%PubOut%\SageHavokEditor.exe" "%DistDir%\" >nul
if errorlevel 1 goto :fail

for /f "delims=" %%v in ('powershell -NoProfile -Command "(Get-Item '%DistDir%\SageHavokEditor.exe').VersionInfo.ProductVersion.Split('+')[0]"') do set "Ver=%%v"
SET "Zip=%~dp0SageHavokEditor_v%Ver%.zip"

echo Zipping...
if exist "%Zip%" del "%Zip%"
powershell -NoProfile -Command "Compress-Archive -Path '%DistDir%\*' -DestinationPath '%Zip%'"
if errorlevel 1 goto :fail

echo ---------------------------------------
echo Done! Upload this:
echo   %Zip%
goto :end

:fail
echo ---------------------------------------
echo FAILED - see the error above.
exit /b 1

:end
endlocal
pause
