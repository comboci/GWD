# V267 — Simplified email notifications

Open **Actions > Email Notifications...**. The former File > Settings button was removed; all unrelated Actions and Settings remain unchanged.

- Select Gmail (default when not yet configured), Yahoo Mail, iCloud Mail, or Custom SMTP. Built-in provider endpoints are defined in `Services/EmailProviderPresets.cs`; there is no editable SMTP host/port for a built-in provider. Custom exposes the host and STARTTLS port.
- Enter the sender address, optional different SMTP login, app password and recipient. Blank login uses the sender address. The password is cleared in the dialog if the provider selection changes; Cancel leaves saved settings untouched.
- Select Success, Failure / recovery failure and/or Cancelled, enable notifications, then Save. Enabled notifications require at least one selected result. Send Test Email checks SMTP independently of the result selection and does not save.
- V266 saved host/port pairs are mapped to matching presets; other configured endpoints remain Custom. The DPAPI CurrentUser registry format and existing result dispatch are unchanged. Presets contain no credentials.
- OAuth-only accounts, including Outlook.com, and implicit TLS port 465 are still unsupported. App passwords depend on account policy.

Provider reference documentation: https://support.google.com/a/answer/176600 ; https://help.yahoo.com/kb/SLN4075.html ; https://support.apple.com/en-us/102525 ; https://support.microsoft.com/en-us/outlook/pop-imap-and-smtp-settings-for-outlook-com .

## Validation

Source checks: menu relocation, preset host/port resolution, legacy endpoint matching, outcome selection and version consistency reviewed. Windows build, UI execution, actual SMTP and deployment integration NOT_RUN in this environment.

Windows runtime checklist:
1. Open Actions and the dialog, check that the former Settings button is absent.
2. Select each preset; SMTP host/port are hidden. Custom reveals both; switching to another provider clears the password. Verify old saved endpoints and selections survive reopening and Cancel.
3. Enable only Success, then only Failure; save/reopen and verify each. Check cancellation and that enabling with no outcome selected is rejected.
4. Send a real test email using an app password, confirm recipient inbox, then perform successful/failed deployment runs and verify only the selected outcomes are delivered.
