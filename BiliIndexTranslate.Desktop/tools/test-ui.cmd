@echo off
rem Runs the UI tests (needs an interactive Windows desktop: the tests drive the real keyboard and mouse).
rem They start the built app and the test target window, so build the solution first. Do not touch the
rem mouse or keyboard while they run. Screenshots of failures go to TestResults\ui-screenshots.
setlocal
cd /d "%~dp0.."
dotnet build BiliIndexTranslate.sln -c Release --nologo -v q || exit /b 1
dotnet test tests\BiliIndexTranslate.App.UiTests\BiliIndexTranslate.App.UiTests.csproj -c Release --no-build --nologo --logger "console;verbosity=normal"
endlocal
