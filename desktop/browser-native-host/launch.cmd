@echo off
setlocal
set "HOST_DIR=%~dp0"
node "%HOST_DIR%src\host.js"
