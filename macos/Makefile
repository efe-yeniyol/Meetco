.PHONY: build debug run install test clean icon open dmg

build:            ## Build Meetco.app (Release) into ./build
	scripts/build.sh Release

debug:            ## Build Meetco.app (Debug) into ./build
	scripts/build.sh Debug

run:              ## Build and launch Meetco
	scripts/run.sh

dmg:              ## Build Release and package build/Meetco-macOS.dmg
	scripts/build.sh Release
	scripts/make-dmg.sh

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
