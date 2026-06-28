# Amphora

A Service Fabric stateless service that exposes [PithosDB](../../README.md) as an HTTP/REST microservice. All PithosDB operations — reads, writes, deletes, scans, and atomic batches — are available over a versioned REST API.

---

## API Reference

All endpoints are prefixed with `/v1`. Keys are UTF-8 strings in the URL path. Values are raw bytes.

### Key encoding

Keys appear as path segments and are URL-decoded by the framework. A key of `user:42` maps to the path `/v1/keys/user:42`. Keys may contain forward slashes — the catch-all route handles them correctly.

### Value encoding

| Context | Encoding |
|---|---|
| `GET /v1/keys/{key}` response body | Raw bytes (`application/octet-stream`) |
| `PUT /v1/keys/{key}` request body | Raw bytes (`application/octet-stream`) |
| `GET /v1/scan` response | Base64 string in JSON |
| `POST /v1/batch` request | Base64 string in JSON |

---

### `GET /v1/keys/{key}`

Returns the value stored at `key`.

**Responses**

| Status | Description |
|---|---|
| `200 OK` | Value bytes as `application/octet-stream` |
| `404 Not Found` | Key does not exist, has been deleted, or has expired |

**Example**

```http
GET /v1/keys/hello
```

```
HTTP/1.1 200 OK
Content-Type: application/octet-stream

world
```

---

### `PUT /v1/keys/{key}`

Inserts or updates `key` with the request body as its value.

**Request**

| Header | Value |
|---|---|
| `Content-Type` | `application/octet-stream` |

**Responses**

| Status | Description |
|---|---|
| `204 No Content` | Write succeeded |

**Example**

```http
PUT /v1/keys/hello
Content-Type: application/octet-stream

world
```

---

### `DELETE /v1/keys/{key}`

Deletes `key` by writing a tombstone. Returns `204` whether or not the key previously existed.

**Responses**

| Status | Description |
|---|---|
| `204 No Content` | Delete succeeded |

**Example**

```http
DELETE /v1/keys/hello
```

---

### `GET /v1/scan`

Returns all live key-value pairs in sorted key order. Supply at most one of `prefix` or `from`/`to`.

**Query parameters**

| Parameter | Description |
|---|---|
| `prefix` | Return only keys that begin with this string |
| `from` | Inclusive lower bound (omit for open-ended) |
| `to` | Inclusive upper bound (omit for open-ended) |

Omitting all parameters returns every key in the database.

**Response**

```json
[
  { "key": "hello", "value": "d29ybGQ=" },
  { "key": "user:1", "value": "YWxpY2U=" }
]
```

`value` is the raw value bytes encoded as Base64.

**Responses**

| Status | Description |
|---|---|
| `200 OK` | JSON array, empty if no keys match |

**Examples**

```http
GET /v1/scan
GET /v1/scan?prefix=user:
GET /v1/scan?from=a&to=z
```

---

### `POST /v1/batch`

Applies a list of `put` and `delete` operations atomically. Either every operation is committed or none are — the entire batch is written to the WAL as a single CRC-guarded record before any mutation occurs.

**Request body**

```json
[
  { "op": "put",    "key": "foo", "value": "YmFy" },
  { "op": "delete", "key": "baz" }
]
```

| Field | Type | Description |
|---|---|---|
| `op` | `"put"` \| `"delete"` | Operation type |
| `key` | `string` | UTF-8 key |
| `value` | `string` (Base64) | Value bytes, required for `put`, omitted for `delete` |

**Responses**

| Status | Description |
|---|---|
| `204 No Content` | Batch committed |
| `400 Bad Request` | Malformed request body |

---

### `GET /v1/stats`

Returns a point-in-time snapshot of database internals.

**Response**

```json
{
  "memTableSizeBytes": 131072,
  "memTableEntryCount": 512,
  "levelCount": 2,
  "fileCountPerLevel": [3, 1],
  "diskSizeBytesPerLevel": [786432, 1048576],
  "totalSstFileCount": 4,
  "totalSstDiskSizeBytes": 1835008,
  "blockCacheCurrentSizeBytes": 4194304,
  "blockCacheMaxSizeBytes": 8388608,
  "walSizeBytes": 65536
}
```

| Field | Description |
|---|---|
| `memTableSizeBytes` | Bytes buffered in the MemTable, not yet flushed to disk |
| `memTableEntryCount` | Entries in the MemTable, including tombstones |
| `levelCount` | Number of active SSTable levels |
| `fileCountPerLevel` | SSTable file count per level (L0 first) |
| `diskSizeBytesPerLevel` | On-disk bytes per level (L0 first) |
| `totalSstFileCount` | Total SSTable files across all levels |
| `totalSstDiskSizeBytes` | Total on-disk bytes across all levels |
| `blockCacheCurrentSizeBytes` | Current block cache occupancy; `-1` if disabled |
| `blockCacheMaxSizeBytes` | Block cache capacity; `-1` if disabled |
| `walSizeBytes` | Current WAL file size; `-1` for in-memory databases |

---

## Configuration

Amphora is configured via `appsettings.json`:

```json
{
  "Amphora": {
    "DataDirectory": "data"
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Amphora:DataDirectory` | `data` (next to the binary) | Path to the PithosDB data directory |

PithosDB tuning options (block cache size, compression, TTL, etc.) are set in [Startup.cs](Startup.cs) by constructing `PithosDb` with a `PithosOptions` instance.

---

## Deployment

Amphora ships as a Service Fabric stateless service.

**Application package:** `server/Amphora.Application/`

| Manifest value | Value |
|---|---|
| Application type | `AmphoraApplicationType` |
| Service type | `AmphoraServiceType` |
| Default endpoint | HTTP port `5174` |
| Default instance count | `-1` (one per node) |

**ApplicationParameters**

| File | `AmphoraService_InstanceCount` | Use |
|---|---|---|
| `Local.1Node.xml` | `1` | Local single-node cluster |
| `Local.5Node.xml` | `-1` | Local five-node cluster |
| `Cloud.xml` | `-1` | Production cluster |

---

## Building

```bash
dotnet build server/Amphora/Amphora.csproj
```

Requires .NET 9 SDK and the Service Fabric SDK (8.5.x).
