# Agent Notes

## Project Shape
- Cloudflare Workers serves static assets from `output/` (`wrangler.jsonc`); `output/` is generated and ignored, so do not hand-edit it.
- Source content is `articles/**/*.md`; site styling starts in `assets/style.css`.
- The static site generator is the C# project in `build/`, targeting `net10.0`; `build/bin/` and `build/obj/` are build artifacts.

## Commands
- Regenerate the site locally with `dotnet run --project build -- --force`.
- `npm run build:worker` runs `scripts/build-worker.sh`: it requires bash, unshallows CI clones, ensures a .NET 10 SDK under `$DOTNET_ROOT`, then runs the generator with the repo URL and current HEAD.
- `npm run dev` / `npm start` run `wrangler dev`; rebuild `output/` first after article, generator, or CSS changes.
- `npm run deploy:preview` runs `wrangler versions upload`; `npm run deploy` runs `wrangler deploy`.
- There are no configured lint, test, or typecheck scripts in `package.json`; use the generator command above as the focused verification step.

## Article Rules
- Every article must be Markdown with YAML front matter containing `title: ...`; the generator throws if the title is missing.
- Non-Markdown files under `articles/` are rejected. Do not create `articles/assets/` or `articles/page/`; those names are reserved by the generator.
- `--- MORE ---` is a custom Markdig block. At most one may appear in an article; it becomes `#read-more` and trims index-page excerpts at that point.
- Article dates come from `git log --follow`: first-add commit is publish time, latest modify commit is edit time. Uncommitted local articles use the current build time outside CI.

## Gotchas
- `assets/style.css` begins with vendored `normalize.css v8.0.1`; CI-style forced builds (`CI=true`) fetch the latest normalize.css and fail if that version marker changes.
- The build script intentionally ignores `package-lock.json`; `.gitignore` excludes it.
