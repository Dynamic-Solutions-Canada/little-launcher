# OAuth loopback sign-in regressions

Run `npm test` (or `dotnet run --project tests/LittleLauncher.OAuth.Tests` with .NET 10). This links the production `OAuthPkceClient` and `CloudTokens` and drives the real sign-in flow with HTTP requests on localhost: a stand-in browser sends crafted callbacks to the app's loopback listener, and a fake token endpoint records what the app sends. Only Windows-protected token storage (in memory here) and logging are adapted.

It covers:

- a callback with another attempt's state, or none, is rejected (400) and never exchanged;
- duplicate `code` parameters, `code` together with `error`, and a wrong path are rejected;
- an `error` callback fails the attempt without contacting the token endpoint;
- a valid code is exchanged exactly once, with the same redirect URI and a PKCE verifier that matches the S256 challenge from the authorize request.

These cases are the evidence behind dismissing CodeQL alert `cs/user-controlled-bypass` on the callback branch (see the comment in `OAuthPkceClient.cs`). Disabling the state check makes the first case fail.

It does not prove anything about the real providers (Microsoft and Google endpoints, consent screens, refresh behaviour), the system browser, or DPAPI storage. Read [docs/sync.md](../../docs/sync.md) before changing the flow.
