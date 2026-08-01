#!/bin/bash
set -e

IMAGE_NAME="${1:-campaignvault:latest}"
DOCKER_BUILDKIT=1

echo "Building Docker image with buildx: $IMAGE_NAME"

# Check if buildx is available
if ! docker buildx version &>/dev/null; then
  echo "buildx not found. Installing buildx..."
  docker run --privileged --rm tonistiigi/binfmt --install all
fi

# Use buildx to build the image
docker buildx build \
  --load \
  -t "$IMAGE_NAME" \
  -f Dockerfile \
  .

echo "✓ Build complete: $IMAGE_NAME"
