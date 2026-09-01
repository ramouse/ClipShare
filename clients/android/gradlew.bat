@ECHO OFF
SETLOCAL
SET "A1_ROOT=%~dp0"
CALL "%A1_ROOT%crypto-contract\gradlew.bat" -p "%A1_ROOT%" %*
EXIT /B %ERRORLEVEL%
