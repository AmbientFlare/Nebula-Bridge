# Resolve the release repository from the origin remote rather than letting gh
# guess. This checkout also has an 'upstream' remote pointing at the project it
# was forked from, and gh picks that one, dispatching releases at the wrong
# repository.
RELEASE_REPO ?= $(shell git remote get-url origin | sed -E 's#^(git@github.com:|https://github.com/)##; s#\.git$$##')

release:
	@echo "Fetching tags..."
	git fetch --tags
	@echo "Bumping version with git-cliff..."
	$(eval NEW_VERSION := $(shell git cliff --bumped-version))
	@echo "New version will be: $(NEW_VERSION)"
	@echo "Generating changelog..."
	@git cliff --unreleased --tag $(NEW_VERSION) --strip all > /tmp/release_notes.md
	@echo "Updating version in build.yaml..."
	sed -i 's/^version: .*/version: "$(NEW_VERSION:v%=%)"/' build.yaml
	@echo "Updating assembly version in NebulaBridge.csproj..."
	@# Must match build.yaml: Jellyfin compares the manifest version against the
	@# installed assembly, so drift makes it re-offer the same update forever.
	sed -i 's#<Version>.*</Version>#<Version>$(NEW_VERSION:v%=%)</Version>#' NebulaBridge.csproj
	git add build.yaml NebulaBridge.csproj
	git commit -m "chore(release): bump version to $(NEW_VERSION)"
	@echo "Pushing to git..."
	git push
	@echo "Creating GitHub release..."
	gh release create $(NEW_VERSION) --repo $(RELEASE_REPO) --title "$(NEW_VERSION)" --notes-file /tmp/release_notes.md
	@echo "Release $(NEW_VERSION) created successfully!"

test:
	@echo "Fetching tags..."
	git fetch --tags
	@echo "Bumping version with git-cliff..."
	$(eval NEW_VERSION := $(shell git cliff --bumped-version))
	@echo "New version will be: $(NEW_VERSION)"
	@echo "Generating changelog..."
	@git cliff --unreleased --tag $(NEW_VERSION) --strip all > /tmp/release_notes.md
	@echo "Release repo would be: $(RELEASE_REPO)"
	@cat /tmp/release_notes.md

.PHONY: release test
