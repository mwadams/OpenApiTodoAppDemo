#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regenerates all Corvus.Json OpenAPI server and client code from specs.
.DESCRIPTION
    Runs corvusjson code generation for each target.
    The broker has two separate specs (frontend + runtime) generating into separate namespaces.
    Path filtering is used for the broker's callback client to only generate from the callback endpoint.
#>

param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Clean) {
    Write-Host "Cleaning generated directories..." -ForegroundColor Cyan
    @(
        "$root/src/TodoApp.Api/Generated/Server",
        "$root/src/TodoApp.Api/Generated/Directory",
        "$root/src/TodoApp.Api/Generated/DirectoryStorage",
        "$root/src/TodoApp.Api/Generated/BrokerClient",
        "$root/src/TodoApp.Identity/Generated/Server",
        "$root/src/TodoApp.Broker/Generated/Frontend",
        "$root/src/TodoApp.Broker/Generated/Runtime",
        "$root/src/TodoApp.Broker/Generated/ApiClient"
    ) | ForEach-Object {
        if (Test-Path $_) { Remove-Item -Recurse -Force $_ }
    }
}

Write-Host "`n=== API Server (from todo-api.json) ===" -ForegroundColor Green
corvusjson openapi-server "$root/specs/todo-api.json" `
    --outputPath "$root/src/TodoApp.Api/Generated/Server" `
    --rootNamespace TodoApp.Api.Server

Write-Host "`n=== API Directory Server (from directory-api.json) ===" -ForegroundColor Green
corvusjson openapi-server "$root/specs/directory-api.json" `
    --outputPath "$root/src/TodoApp.Api/Generated/Directory" `
    --rootNamespace TodoApp.Api.Directory

Write-Host "`n=== API Directory Storage Types (from directory-storage.json) ===" -ForegroundColor Green
corvusjson jsonschema "$root/specs/directory-storage.json" `
    --outputPath "$root/src/TodoApp.Api/Generated/DirectoryStorage" `
    --rootNamespace TodoApp.Api.DirectoryStorage `
    --outputRootTypeName Directory

Write-Host "`n=== Identity Server (from identity-api.json) ===" -ForegroundColor Green
corvusjson openapi-server "$root/specs/identity-api.json" `
    --outputPath "$root/src/TodoApp.Identity/Generated/Server" `
    --rootNamespace TodoApp.Identity.Server

Write-Host "`n=== API's Broker Client (from broker-runtime.json) ===" -ForegroundColor Green
corvusjson openapi-client "$root/specs/broker-runtime.json" `
    --outputPath "$root/src/TodoApp.Api/Generated/BrokerClient" `
    --rootNamespace TodoApp.Api.BrokerClient

Write-Host "`n=== Broker Frontend Server (from broker-frontend.json) ===" -ForegroundColor Green
corvusjson openapi-server "$root/specs/broker-frontend.json" `
    --outputPath "$root/src/TodoApp.Broker/Generated/Frontend" `
    --rootNamespace TodoApp.Broker.Frontend

Write-Host "`n=== Broker Runtime Server (from broker-runtime.json) ===" -ForegroundColor Green
corvusjson openapi-server "$root/specs/broker-runtime.json" `
    --outputPath "$root/src/TodoApp.Broker/Generated/Runtime" `
    --rootNamespace TodoApp.Broker.Runtime

Write-Host "`n=== Broker's API Callback Client (from todo-api.json, callbacks only) ===" -ForegroundColor Green
corvusjson openapi-client "$root/specs/todo-api.json" `
    --outputPath "$root/src/TodoApp.Broker/Generated/ApiClient" `
    --rootNamespace TodoApp.Broker.ApiClient `
    --include-path "/callbacks/**"

Write-Host "`nCode generation complete." -ForegroundColor Green
