#!/bin/bash

set -e  # Exit on any error

# Build each Lambda function
echo "Building Lambdas..."

for lambda_dir in lambdas/*; do
  if [[ -d "$lambda_dir" ]]; then
    project_name=$(basename "$lambda_dir")
    echo "Building $project_name..."
    dotnet publish "$lambda_dir/$project_name.csproj" -c Release -o "$lambda_dir/publish"
  fi
done

# Build and deploy the CDK stack
echo "Building and deploying CDK project..."

dotnet build src/Altusproj/Altusproj.csproj
cdk deploy --require-approval never --profile altus "$@"

echo "Build and deploy complete."

