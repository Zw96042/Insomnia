# Lint settings for the install scripts. CI runs Invoke-ScriptAnalyzer with them.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # Progress lines are meant for the person running the installer.
        'PSAvoidUsingWriteHost',
        # The helpers in common.ps1 are private to the two scripts, which are
        # all-or-nothing operations; -WhatIf support would be misleading.
        'PSUseShouldProcessForStateChangingFunctions'
    )
}
