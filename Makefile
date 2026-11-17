.PHONY: build debug run install test clean icon open check-secrets hooks

build:            ## Build Meetco.app (Release) into ./build
	scripts/build.sh Release

debug:            ## Build Meetco.app (Debug) into ./build
	scripts/build.sh Debug

run:              ## Build and launch Meetco
	scripts/run.sh

install:          ## Build and install to /Applications
	scripts/install.sh

test:             ## Run unit tests
	xcodebuild -project Meetco.xcodeproj -scheme Meetco -derivedDataPath build/DerivedData -destination "platform=macOS" test

icon:             ## Regenerate the app icon
	swift scripts/generate-icon.swift

open:             ## Open the project in Xcode
	open Meetco.xcodeproj

clean:            ## Remove build output
	rm -rf build

check-secrets:    ## Fail if anything that looks like a credential would be published
	scripts/check-secrets.sh

hooks:            ## Install a git pre-commit hook that blocks committed secrets
	printf '#!/bin/sh\nexec scripts/check-secrets.sh --staged\n' > .git/hooks/pre-commit
	chmod +x .git/hooks/pre-commit
	@echo "✅ pre-commit secret check installed"
