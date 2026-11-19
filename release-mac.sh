#!/usr/bin/env bash
# Adds what only a Mac can build to a release that release.ps1 has published:
#
#   usagetop-mac.dmg                 the Mac app, universal, signed and notarized,
#                                    in a disk image to drag it to Applications
#                                    from, attached to the release; the app
#                                    updates itself from it too
#   ghcr.io/rzukic/usagetop-server   the server's image for linux/amd64 and
#                                    linux/arm64, tagged <version> and latest
#
#   ./release-mac.sh            the latest published release
#   ./release-mac.sh 1.2.1      that release
#   ./release-mac.sh 1.2.1 mac      only the app
#   ./release-mac.sh 1.2.1 image    only the image
#
# It builds the commit that is checked out, and refuses to run with uncommitted
# changes or from a commit that is not on GitHub's main branch. The image is tried out here before it is
# pushed: started on an empty volume, it must serve its page and write to /data.
# A pre-release is tagged with its version only; latest stays on the last
# stable release.
#
# Needs:
#   - Xcode 26, and create-dmg (brew install create-dmg); create-dmg has
#     Finder lay out the image's window, so the terminal must be allowed to
#     control Finder
#   - for a Mac app that opens without a Gatekeeper warning: a Developer ID
#     Application certificate in the keychain, and notary credentials stored as
#     the profile "usagetop" (or the one USAGETOP_NOTARY_PROFILE names):
#         xcrun notarytool store-credentials usagetop --apple-id <id> --team-id <team>
#     Without a certificate the app is signed ad hoc and the image not at all,
#     and whoever downloads it
#     has to allow it by hand. With one but no credentials the script stops:
#     a signed app that is not notarized is refused just the same.
#   - gh, signed in to an account that may write to the repository; for the
#     image also with the write:packages scope:
#         gh auth refresh -s write:packages
#   - Docker, running
set -euo pipefail

cd "$(dirname "$0")"

REPO="rzukic/usagetop"
IMAGE="ghcr.io/rzukic/usagetop-server"
BUILDER="usagetop"
OUT="build/release"

VERSION="${1:-}"
WHAT="${2:-all}"
case "$WHAT" in
all | mac | image) ;;
*)
	echo "Unknown part '$WHAT': mac, image, or nothing for both." >&2
	exit 1
	;;
esac

step() { printf '\n== %s\n' "$1"; }
fail() {
	echo "$1" >&2
	exit 1
}

if [[ -z "$VERSION" ]]; then
	VERSION="$(gh release view -R "$REPO" --json tagName --jq .tagName)"
fi
VERSION="${VERSION#v}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || fail "'$VERSION' is not a version like 1.2.0 or 1.2.1-beta.1."
TAG="v$VERSION"
echo "usagetop $VERSION"

# What is added to a release is a commit that is on GitHub: nothing
# uncommitted, nothing unpushed.
[[ -z "$(git status --porcelain)" ]] || fail "There are uncommitted changes. Commit or stash them first."
SHA="$(git rev-parse HEAD)"
WHERE="$(gh api "repos/$REPO/compare/main...$SHA" --jq .status 2>/dev/null || true)"
[[ "$WHERE" == "identical" || "$WHERE" == "behind" ]] || fail "HEAD (${SHA:0:7}) is not on GitHub's main branch. Push it first."
gh release view "$TAG" -R "$REPO" >/dev/null 2>&1 || fail "There is no release $TAG. Publish it with release.ps1 first."

mkdir -p "$OUT"

if [[ "$WHAT" != "image" ]]; then
	step "Mac: build"
	VERSION="$VERSION" ./my-usage-mac/build.sh release
	APP="my-usage-mac/usagetop.app"
	[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")" == "$VERSION" ]] ||
		fail "usagetop.app does not say $VERSION."
	ARCHS="$(lipo -archs "$APP/Contents/MacOS/AiUsageMac")"
	[[ "$ARCHS" == *arm64* && "$ARCHS" == *x86_64* ]] || fail "usagetop.app is not universal: $ARCHS."
	codesign --verify --strict "$APP"

	DMG="$OUT/usagetop-mac.dmg"
	rm -f "$DMG"

	# Signed with a Developer ID, the app goes to Apple's notary service, and
	# the ticket that comes back is stapled to it, so it also opens offline;
	# then the same for the disk image it is packed in.
	# Read whole first: grep -q stops at the first match, and with pipefail the
	# codesign it cut off would turn a signed app into an unsigned one.
	SIGNED=""
	SIGNATURE="$(codesign -dv "$APP" 2>&1)"
	grep -q '^TeamIdentifier=[A-Z0-9]\{10\}$' <<<"$SIGNATURE" && SIGNED=1
	PROFILE="${USAGETOP_NOTARY_PROFILE:-usagetop}"
	notarize() {
		xcrun notarytool submit "$1" --keychain-profile "$PROFILE" --wait | tee "$OUT/notary.log"
		grep -q 'status: Accepted' "$OUT/notary.log" || fail "Apple did not accept $1; see: xcrun notarytool log <id> --keychain-profile $PROFILE"
		rm -f "$OUT/notary.log"
	}
	if [[ -n "$SIGNED" ]]; then
		step "Mac: notarize the app"
		# The notary service takes a zip, not a bare bundle; ditto keeps the
		# bundle's signature and attributes intact.
		ditto -c -k --keepParent "$APP" "$OUT/usagetop-notarize.zip"
		notarize "$OUT/usagetop-notarize.zip"
		rm -f "$OUT/usagetop-notarize.zip"
		xcrun stapler staple "$APP"
		spctl --assess --type execute "$APP" || fail "Gatekeeper still refuses the app."
	else
		echo "No Developer ID certificate: the app is signed ad hoc and Gatekeeper will stop a download of it."
	fi

	step "Mac: disk image"
	./my-usage-mac/dmg/make-dmg.sh "$APP" "$DMG"
	if [[ -n "$SIGNED" ]]; then
		step "Mac: notarize the disk image"
		notarize "$DMG"
		xcrun stapler staple "$DMG"
		spctl --assess --type open --context context:primary-signature "$DMG" || fail "Gatekeeper refuses the disk image."
	fi

	step "Mac: upload"
	gh release upload "$TAG" -R "$REPO" "$DMG" --clobber
fi

if [[ "$WHAT" != "mac" ]]; then
	docker info >/dev/null 2>&1 || fail "Docker is not running."

	# The stock builder keeps one platform per image; a docker-container
	# builder can push both under one tag.
	docker buildx inspect "$BUILDER" >/dev/null 2>&1 || docker buildx create --name "$BUILDER" --driver docker-container >/dev/null

	build() {
		docker buildx build --builder "$BUILDER" -f my-usage-server/Dockerfile \
			--label "org.opencontainers.image.source=https://github.com/$REPO" \
			--label "org.opencontainers.image.description=usagetop server" \
			--label "org.opencontainers.image.licenses=MIT" \
			--label "org.opencontainers.image.version=$VERSION" \
			--label "org.opencontainers.image.revision=$SHA" \
			--build-arg "VERSION=$VERSION" \
			"$@" .
	}

	# This machine's own architecture, loaded into Docker and run.
	step "Image: build and try out"
	TRY="usagetop-server:try-$VERSION"
	NAME="usagetop-try-$$"
	build --load -t "$TRY"
	cleanup() {
		docker rm -f "$NAME" >/dev/null 2>&1 || true
		docker volume rm "$NAME" >/dev/null 2>&1 || true
	}
	trap cleanup EXIT
	docker run -d --name "$NAME" -p 127.0.0.1::8377 -v "$NAME:/data" "$TRY" >/dev/null
	PORT="$(docker port "$NAME" 8377/tcp | head -n 1 | sed 's/.*://')"
	for _ in $(seq 1 30); do
		[[ "$(curl -fsS "http://127.0.0.1:$PORT/healthz" 2>/dev/null)" == "ok" ]] && break
		sleep 1
	done
	[[ "$(curl -fsS "http://127.0.0.1:$PORT/healthz")" == "ok" ]] || {
		docker logs "$NAME" >&2
		fail "The image did not answer on port 8377."
	}
	curl -fsS "http://127.0.0.1:$PORT/" | grep -qi '<html' || fail "The image does not serve its page."
	[[ "$(docker exec "$NAME" id -u)" != "0" ]] || fail "The image runs as root."
	docker exec "$NAME" test -s /data/usage.db || fail "The image did not write its database to /data."
	docker exec "$NAME" test -s /data/api-token || fail "The image did not write its token to /data."
	cleanup
	trap - EXIT
	docker image rm "$TRY" >/dev/null

	step "Image: push"
	gh auth status 2>&1 | grep -q "write:packages" || fail "gh may not push packages. Run: gh auth refresh -s write:packages"
	gh auth token | docker login ghcr.io -u "$(gh api user --jq .login)" --password-stdin >/dev/null
	TAGS=(-t "$IMAGE:$VERSION")
	[[ "$VERSION" == *-* ]] || TAGS+=(-t "$IMAGE:latest")
	build --platform linux/amd64,linux/arm64 "${TAGS[@]}" --push
	docker buildx imagetools inspect "$IMAGE:$VERSION"
fi

printf '\nDone: https://github.com/%s/releases/tag/%s\n' "$REPO" "$TAG"
