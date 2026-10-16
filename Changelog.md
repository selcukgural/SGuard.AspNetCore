# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `AddSGuardProblemDetails` and `SGuardExceptionHandler`: return SGuard guard failures as `400 Bad Request` validation
  problem details (RFC 9457), keyed by the failing member and with a fixed message per guard that never includes the
  checked value or code.
- `SGuardProblemDetailsOptions`: the status code, the error key and message, and more exception types to handle.
