Send @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{ name='gx770-live'; version='1.0' } } }
$null = ReadId 1 30
Send @{ jsonrpc='2.0'; method='notifications/initialized' }
$null = Read-Ready 2 'genexus_lifecycle' @{ action='status'; wait=20 } 60
