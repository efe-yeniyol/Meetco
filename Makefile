# Meetco — shortcuts for both apps. Run `make help` to list them.
# The macOS app lives in macos/, the Windows app in windows/.

.PHONY: help install run build dmg test open clean windows-test windows-build check-secrets hooks

help:             ## List the available commands
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | sed 's/:.*## /\t/' | expand -t 18

# macOS app (needs Xcode 26)
install:          ## macOS: build Release and install to /Applications
	$(MAKE) -C macos install

run:              ## macOS: build Release and launch
	$(MAKE) -C macos run

build:            ## macOS: build Release into macos/build
	$(MAKE) -C macos build

dmg:              ## macOS: build Release and package macos/build/Meetco-macOS.dmg
	$(MAKE) -C macos dmg

test:             ## macOS: run unit tests
	$(MAKE) -C macos test

open:             ## macOS: open the Xcode project
	$(MAKE) -C macos open

clean:            ## Remove build output of both apps
	$(MAKE) -C macos clean
	rm -rf windows/publish windows/src/*/bin windows/src/*/obj windows/tests/*/bin windows/tests/*/obj

# Windows app (needs the .NET 10 SDK; the core tests also run on macOS and Linux)
windows-test:     ## Windows: run the core unit tests
	cd windows && dotnet test tests/Meetco.Core.Tests

windows-build:    ## Windows: publish a self-contained Meetco.exe into windows/publish/win-x64
	cd windows && dotnet publish src/Meetco.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Repository hygiene
check-secrets:    ## Fail if anything that looks like a credential would be published
	scripts/check-secrets.sh

hooks:            ## Install a git pre-commit hook that blocks committed secrets
	printf '#!/bin/sh\nexec scripts/check-secrets.sh --staged\n' > .git/hooks/pre-commit
	chmod +x .git/hooks/pre-commit
	@echo "✅ pre-commit secret check installed"
