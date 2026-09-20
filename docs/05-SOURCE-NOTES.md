# Upstream checks for implementation

Checked on 2026-09-20. Recheck these primary sources when selecting a Valheim build, implementing graceful stop, or exposing the public control endpoint.

- [Valheim: A Guide to Dedicated Servers](https://www.valheimgame.com/support/a-guide-to-dedicated-servers/): Steam installation steps; Windows `start_headless_server.bat`; default game ports 2456 and 2457; Steam backend port-forwarding requirement; Crossplay relay behavior; readiness message; `-savedir`; `permittedlist.txt`; and Ctrl+C shutdown guidance. Do not infer from this guide alone that a process-exists check proves readiness or a save completed.
- [Let's Encrypt: IP address certificates generally available](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability): a public IP can have a publicly trusted certificate, but the currently documented IP certificates last about 160 hours and require reliable automated renewal. TogetherServer's initial design prefers certificate fingerprint pinning in a manually shared Friend invite; verify the final pairing implementation and threat model before public use.
- [Microsoft: Configure Kestrel HTTPS endpoints](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0): a public ASP.NET Core HTTPS listener needs explicit certificate configuration. A local development certificate is not a production/public trust solution.
- [OWASP REST Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html): use HTTPS, per-request authentication/authorization, restricted methods, rate limits, and careful handling of management endpoints and secrets.

These links are engineering inputs, not acceptance evidence. Real Valheim behavior, remote reachability, save/restart, and Friend notifications must be tested on the owner's actual setup without altering public firewall/router/DNS settings or game terms automatically.
