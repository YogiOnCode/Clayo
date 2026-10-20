# Contributing

Open an issue before a big change so we can agree on the approach first.

1. **Branch** from `main`, one change per branch.
2. **Build and check** with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

   ```powershell
   dotnet build src/Clayo -warnaserror
   .\scripts\checks.ps1            # every check; exits with the number that failed
   .\scripts\checks.ps1 status     # or only the ones you name
   ```

   A check is a file-based app in `checks/` that prints `PASS` / `FAIL` per case and exits
   with its failure count. Add or extend one for what you change. Checks work in temp
   folders and throwaway registry keys, never your real settings.
3. **Open a pull request** to `main`. CI runs the same build and checks; it has to be green,
   and the PR needs one review. Pull requests are squash-merged.

Found a security issue? Report it privately from the **Security** tab, not in an issue.
