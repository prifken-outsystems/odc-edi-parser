# Deploy OutSystems Extension

Deploy the EdiParserLibrary C# extension to OutSystems ODC.
Builds, packages, and pushes to the platform — then returns the approval link.

## Workflow

### 1. Summarise What We're Deploying

Print this narrative block before doing anything:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  OutSystems Extension — Ready to Deploy
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Library : EdiParser
  Built by: Backend Team (C# / .NET 8)
  Purpose : Parses incoming vendor EDI 850 Purchase Order
            documents — extracts vendor identity, line items,
            amounts, and dates with confidence scores.

  This extension was written in C#, in the backend team's
  own environment. They never opened OutSystems Studio.

  Deploying to: https://ailaunchfuture.outsystems.dev
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### 2. Build the Extension

Run the local build script to compile and publish for linux-x64 (ODC Lambda target):

```bash
powershell.exe -ExecutionPolicy Bypass -File build.ps1 -Clean
```

If the build fails, stop and report the error clearly. Do not proceed to deploy.

### 3. Verify the Package

Check the ZIP was created and report its size:

```bash
powershell.exe -Command "if (Test-Path EdiParserLibrary.zip) { $s = (Get-Item EdiParserLibrary.zip).Length/1MB; Write-Host ('Package: EdiParserLibrary.zip (' + $s.ToString('F2') + ' MB) — ready') } else { Write-Error 'ZIP not found'; exit 1 }"
```

### 4. Commit and Push to Trigger Deployment

Stage and push any changes. The GitHub Actions pipeline handles the rest:

```bash
git add -A && git diff --cached --quiet || git commit -m "Deploy: EdiParser extension update" && git push origin main
```

If there are no changes to commit (already up to date), just push:

```bash
git push origin main
```

### 5. Get the GitHub Actions Run URL

Retrieve the URL of the triggered workflow run:

```bash
gh run list --repo prifken-outsystems/odc-edi-parser --limit 1 --json url,status,name 2>/dev/null
```

Wait up to 10 seconds and retry once if the run hasn't appeared yet.

### 6. Print the Deployment Status Block

Print this block with the actual run URL filled in:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Deployment Pipeline — Running
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Step 1  Authenticate with ODC          ⏳
  Step 2  Get pre-signed upload URL      ⏳
  Step 3  Upload package to platform     ⏳
  Step 4  Start library generation       ⏳
  Step 5  Poll generation status         ⏳
  Step 6  Tag revision                   ⏳

  Pipeline: <GITHUB_ACTIONS_RUN_URL>

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  When generation completes, review and approve at:

  https://ailaunchfuture.outsystems.dev
  → Libraries → External Libraries → EdiParser

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  Every OutSystems app in this environment can now
  use the EDI parser — built in C#, by the backend
  team, in their own tools.

  The platform didn't change. It just got more capable.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Notes

- The deployment pipeline runs on GitHub Actions (2–4 minutes end-to-end)
- If ODC returns `ReadyForReview` instead of `Completed`, manual approval is required at the link above — this is ODC's security review gate
- The `GetBuildVersion()` method in `EdiParser.cs` is automatically updated by CI/CD to force a new library revision on every build
- Do not manually upload ZIPs to ODC — always use this pipeline
