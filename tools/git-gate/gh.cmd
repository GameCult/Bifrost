@echo off
setlocal

set "REAL_GH=%BIFROST_REAL_GH%"
if not defined REAL_GH (
  echo Bifrost GitHub gate has no real GitHub CLI executable configured.>&2
  exit /b 1
)

set "NODE_EXE=%BIFROST_NODE_EXECUTABLE%"
if not defined NODE_EXE (
  echo Bifrost GitHub gate has no Node executable configured.>&2
  exit /b 1
)

set "NODE_SCRIPT=%~dp0gh-proxy.mjs"
"%NODE_EXE%" "%NODE_SCRIPT%" %*
exit /b %ERRORLEVEL%
