@echo off
setlocal
rem Publishes flp-util as a trimmed, self-contained, single-file Windows x64 exe.
rem Output: dist\flp-util-x64.exe
rem
rem Requires the .NET 10 SDK. Note the TrimmerRootAssembly entry in FlpUtil.csproj:
rem Lucene.NET discovers its codecs by reflection, so trimming must keep it whole.

cd /d "%~dp0"

rem IL2104 is the trimmer's "this third-party assembly produced trim warnings" rollup; Lucene.NET
rem and J2N are not trim-annotated, so it always fires and TreatWarningsAsErrors would fail the
rem publish. Safety comes from the TrimmerRootAssembly instead, verified by running the exe.
dotnet publish src\FlpUtil -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:PublishTrimmed=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:NoWarn=IL2104 ^
  -o dist\publish
if errorlevel 1 exit /b 1

copy /y dist\publish\flp-util.exe dist\flp-util-x64.exe >nul
if errorlevel 1 exit /b 1

for %%F in (dist\flp-util-x64.exe) do echo Built %%F (%%~zF bytes)
endlocal
