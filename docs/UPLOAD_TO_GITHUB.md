# Upload v1.0.0 yourself

This guide publishes the prepared release as a new repository. You perform every
GitHub action yourself. The pipeline is:

**Prepared folder → local Git commit → GitHub push → v1.0.0 tag → GitHub Release.**

The repository displays your README, code and evidence. The tag identifies the
exact version. The GitHub Release adds a public description and downloadable ZIP.

## 1. Use a clean folder

Extract the prepared `sts2-two-agent-v1.0.0.zip` into a new location outside your
research project, such as your Desktop. Open the extracted
`sts2-two-agent-v1.0.0` folder: `README.md`, `LICENSE`, `models/` and `results/`
should be directly inside it.

Use that extracted folder for these commands. The parent research repository
contains experiments and a different Git remote; it is not the upload folder.

In Terminal, type `cd `, drag the extracted folder into Terminal, then press
Return. Check the prepared files:

```sh
pwd
ls
python3 scripts/verify_release.py
```

The verifier needs only Python 3.9 or newer and should report 256 runs, 68 wins
and 26.5625%. Installing PyTorch or .NET is only necessary to run the agents.

## 2. Create an empty repository on GitHub

Sign in to GitHub, choose **New repository**, and use a name such as
`sts2-two-agent`. Select **Public** so others can see the result. Suggested
description:

> Two-agent RL for Slay the Spire 2: 26.6% best observed validation win rate on Ironclad A2 in a three-act simulator benchmark.

Leave the options for adding a README, `.gitignore`, and license off: these
already exist in your prepared folder. Create the repository and copy its HTTPS
URL. See [GitHub's guide to importing local code](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github).

## 3. Make your first local commit

Run these inside the extracted release folder:

```sh
git init -b main
git add .
git status --short
git commit -m "Release v1.0.0: Ironclad two-agent benchmark"
```

`git add` selects files for the snapshot. `git commit` saves that snapshot
locally. Review the status list before committing: it should contain the
prepared release files, including `models/choice.pt` and `models/battle.pt`.

If Git asks who you are, set your preferred author name and email for this
repository, then repeat the commit command:

```sh
git config user.name "YOUR NAME"
git config user.email "YOUR GITHUB EMAIL"
```

Use your GitHub-provided private email address if you prefer to keep your
personal email out of commits.

## 4. Connect your repository and push

Replace the example URL below with the URL you copied from your own GitHub
repository:

```sh
git remote add origin https://github.com/YOUR_USERNAME/sts2-two-agent.git
git remote -v
git push -u origin main
```

`origin` names the GitHub destination. `push` uploads your commit. Follow the
authentication flow on your machine. If you already have GitHub CLI, you can
sign in with `gh auth login` and configure Git with `gh auth setup-git` first.
GitHub account passwords are not accepted as HTTPS Git passwords; see
[GitHub's authentication options](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/about-authentication-to-github).

Refresh your repository page. Confirm that the README headline, result evidence,
two models, simulator and MIT license appear.

## 5. Create and publish the v1.0.0 release

Label the uploaded commit:

```sh
git tag -a v1.0.0 -m "v1.0.0: two-agent Ironclad validation benchmark"
git push origin v1.0.0
```

On GitHub, open **Releases → Draft a new release**. Choose the existing
`v1.0.0` tag and use the title **v1.0.0 — Ironclad two-agent benchmark**.

Copy the v1.0.0 entry from `CHANGELOG.md` into the description. Attach the
prepared ZIP and its `.sha256` file, preview the page, and click **Publish
release** when you are satisfied. GitHub documents the release form in
[Managing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

The main repository link is the one to share for your results. The Releases
page provides the downloadable versioned package.
