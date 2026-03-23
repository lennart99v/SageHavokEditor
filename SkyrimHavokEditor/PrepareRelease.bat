@echo off
:: Adjusted to match your specific folder structure
SET "ReleaseDir=.\bin\Release\net8.0-windows"
SET "OutDir=.\SkyrimHavokEditor_Dist"

echo Cleaning old distribution folder...
if exist "%OutDir%" rd /s /q "%OutDir%"
mkdir "%OutDir%"

echo Copying fresh Release files...
:: We exclude the .pdb (debug info) to keep it clean for Nexus
xcopy "%ReleaseDir%\SkyrimHavokEditor.exe" "%OutDir%\" /Y
xcopy "%ReleaseDir%\SkyrimHavokEditor.dll" "%OutDir%\" /Y
xcopy "%ReleaseDir%\SkyrimHavokEditor.runtimeconfig.json" "%OutDir%\" /Y
xcopy "%ReleaseDir%\SkyrimHavokEditor.deps.json" "%OutDir%\" /Y

echo ---------------------------------------
echo Done! Check the folder now.
pause