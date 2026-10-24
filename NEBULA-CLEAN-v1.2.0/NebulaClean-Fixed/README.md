# NEBULA CLEAN Advanced + Accounts

Windows WPF app (.NET 10) plus a small ASP.NET Core API for shared accounts, subscriptions, promo codes and forced version updates.

## Important architecture note
The desktop app alone cannot share accounts/promo codes or enforce updates across all users. Deploy the `NebulaServer` project to one HTTPS server and configure `appsettings.json` in the desktop app with that API URL. All users must use the same server/database. Do not expose the SQLite database file publicly.

## Desktop launch
1. Install .NET 10 SDK on Windows.
2. Set `ApiBaseUrl` in `appsettings.json` to your deployed HTTPS API URL.
3. To enable Google login, create an OAuth 2.0 **Desktop app** client in Google Cloud Console, download its JSON, and save it as `client_secrets.json` beside `NebulaClean.csproj`. Never publish credentials for a web client or commit secrets to a public repository. The app uses Google OAuth scopes `openid`, `email`, `profile`.
4. Run `RUN_NEBULA_CLEAN.bat`.

## Backend deploy
1. Publish `NebulaServer` with .NET 10 and deploy behind HTTPS. The host must persist a writable directory for `nebula.db`.
2. Configure environment variables on the server: `NEBULA_ADMIN_EMAIL` and `NEBULA_ADMIN_PASSWORD`. Set these securely in the hosting provider's secret/environment settings; never hardcode the password into source code or the desktop app. The configured email becomes the admin account on first startup.
3. Optionally set `NEBULA_DB_PATH` to a persistent database path. Back up the database regularly.
4. Check `/health` returns `status: ok`.
5. Set a real `downloadUrl` in the database `settings` table before enabling mandatory updates.

## Features in this revision
- Email/password registration and login; passwords are stored as PBKDF2-SHA256 hashes with random salts.
- Google sign-in through Google OAuth; server validates the Google access token against Google userinfo before creating/signing into the account.
- Shared account sessions and subscription state through the API/database.
- Free and Promo plan UI; promo codes have configurable access duration and max number of redemptions.
- Admin-only promo code creation and minimum/latest version publishing in the Subscriptions page.
- Change password for email/password accounts and sign-out from sidebar/settings.
- Version check after login. The API has `latestVersion`, `minimumVersion`, `downloadUrl`, `updateMessage` settings and an admin-only `/api/admin/version` endpoint. Set `minimumVersion` above old client versions to block old versions.

## API version administration
POST `/api/admin/version` with a Bearer admin session and JSON body, e.g. `{"latestVersion":"1.2.0","minimumVersion":"1.2.0","downloadUrl":"https://your-domain/download","message":"Обновитесь до новой версии."}`. Set `minimumVersion` to the lowest version you still allow. To force everyone on older versions to update, increase it. The client blocks entry when current version is lower than minimum and offers the download URL.

## Security and limitations
- This project has not been compiled or security-audited in this environment. Test on a staging server before distributing.
- Put the API behind HTTPS and rate limiting; configure backups and monitoring. For production, add email verification/password reset, abuse protection, audit logs, and a proper release/update installer.
- The admin password is deliberately not embedded in the project. Set it through server environment variables. If a password was shared in chat or another public location, rotate it before production use.
- A Google OAuth desktop client JSON is required for Google login; it cannot be created by the app automatically.
- Promo code duration is the subscription duration after redemption; promo code itself has a long expiry configured by the backend.
