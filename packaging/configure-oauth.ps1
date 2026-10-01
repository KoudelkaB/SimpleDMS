# Google Desktop OAuth clients are public clients. User refresh tokens never enter CI.
if ([string]::IsNullOrWhiteSpace($env:GOOGLE_CLIENT_ID)) {
    Write-Warning "Publisher Google client not configured. Import client JSON in application Settings."
    exit 0
}
if ($env:GOOGLE_CLIENT_ID -notmatch '\.apps\.googleusercontent\.com$') { throw "Invalid Google client ID" }
@{ installed = @{
    client_id = $env:GOOGLE_CLIENT_ID
    client_secret = $env:GOOGLE_CLIENT_SECRET
    auth_uri = "https://accounts.google.com/o/oauth2/auth"
    token_uri = "https://oauth2.googleapis.com/token"
} } | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 oauth-client.json
