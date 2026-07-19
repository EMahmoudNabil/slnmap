# Contributing to Slnmap

Thanks for your interest in Slnmap!

## Issues

Bug reports and feature ideas are welcome — please [open an issue](https://github.com/EMahmoudNabil/slnmap/issues).
Include your OS, .NET SDK version (`dotnet --version`), and, for analysis problems, the output of
`slnmap doctor`.

## Pull requests

Before opening a PR, please **open an issue first to discuss** the change. It saves everyone time and
helps make sure the work lands cleanly — the roadmap is currently driven by beta feedback, so a quick
conversation up front avoids surprises.

For anything you do send:

```console
dotnet build -c Release
dotnet test  -c Release
```

should be green, and new behavior should come with tests.

## Be kind

Be respectful and constructive. We want this to be a friendly place to collaborate.
