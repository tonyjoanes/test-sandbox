# Intentionally empty.
#
# This function authenticates to the internal endpoint with a client
# certificate loaded from the platform certificate store (Cert:\CurrentUser\My)
# and calls Invoke-WebRequest, which ships with PowerShell 7. It does not
# call Azure Resource Manager, so it does not need the Az module.
#
# Importing 'Az' here would add several seconds to every cold start (the
# module tree is large) for zero benefit to this function. Only add a
# managed-dependency entry when a function actually calls an Az cmdlet.
@{
}
