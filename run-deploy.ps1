$target = "$env:LOCALAPPDATA\DevMind"
.\dist\deploy-devmind.ps1 -Source ./publish -Target $target -AddToPath
