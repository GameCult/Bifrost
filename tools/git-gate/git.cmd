@echo off
setlocal

set "NODE_EXE=%BIFROST_NODE_EXECUTABLE%"
if not defined NODE_EXE (
  echo Bifrost Git gate has no Node executable configured.>&2
  exit /b 1
)

set "NODE_SCRIPT=%~dp0git-proxy.mjs"
"%NODE_EXE%" "%NODE_SCRIPT%" %*
exit /b %ERRORLEVEL%
