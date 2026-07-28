# Azure Functions PowerShell profile.ps1
# Runs once per worker cold start, before any function invocation.
#
# Deliberately empty. This app doesn't call Azure Resource Manager, so there
# is no Connect-AzAccount / managed-identity sign-in to do here. If a future
# function needs Az cmdlets, add the module to requirements.psd1 first, then
# sign in here with Connect-AzAccount -Identity.
