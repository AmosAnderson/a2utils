# ADR 0010: expose the CLI through the official MCP SDK

## Status

Accepted.

## Context

Coding agents need a long-lived, typed protocol rather than shell-text parsing. The CLI already has versioned JSON envelopes, schemas, capability discovery, bounded operations, and explicit side-effect metadata. Reimplementing Model Context Protocol framing and version negotiation would duplicate a changing standard and make interoperability difficult to verify.

## Decision

The CLI references the official `ModelContextProtocol` 2.2.0 package and exposes `a2 mcp serve` over stdio. It supports the SDK's current MCP 2026-07-28 protocol and older initialization clients. The server offers three tools: a complete capability catalog, bundled schema retrieval, and bounded invocation of any non-MCP CLI command through the existing `--json` contract.

MCP calls run in the server process working directory. They retain the same validation, transactional writes, cancellation, output envelopes, and exit codes as direct CLI calls. The server refuses recursive MCP invocation and caps each complete tool response at 16 MiB.

## Consequences

Agent hosts can use one persistent A2Utils subprocess and receive both text and structured results. The official SDK adds a maintained protocol dependency to the packaged tool. stdout belongs exclusively to MCP while the server is active; operational logs must use stderr.
