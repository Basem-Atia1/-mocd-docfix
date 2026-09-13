# How mocd_documentfile records are actually created — and what the tool must copy forward

**Status:** Findings, verified against dev and pre-prod
**Date:** 2026-09-13

Written after the operator asked three questions: is the GUID in the path always the CRM record
id, what data do these records actually carry, and what does the upload endpoint fill in.

The short answer to the first is **no** — and the reason matters, because it changes what the
remediation tool has to write.

---

## 1. There are two creation paths, and they behave differently

### Path A — the portal

`MoCD.EServices.Data.CRM\CRM Data Services\General\DocumentDataService.cs`, `UploadDocument()`:

```csharp
var response = FileService.UploadFile(fileUploadRequestModel);   // vendor assigns FileId + path
file.Id = response.Data.FileId;                                  // CRM's key IS the vendor's id
file = ...AddString(file, Name,      document.FileName);
file = ...AddString(file, MediaType, document.mimeType);
file = ...AddString(file, Category,  document.Category);
file = ...AddString(file, Hash,      response.Data.Hash);
file = ...AddString(file, FilePath,  response.Data.FilePath);
service.Create(file);
```

Six fields. The Category comes from `ConfigurationDataService.cs:2461`, which reads the document
type's `mocd_servicecatalogue` — so the path gets a real folder.

### Path B — the CRM plugin

`MOCD.CRM.Plugins.DocumentManagement\UploadDocument.cs`, action `mocd_uploaddocument`:

```csharp
Entity documentFile = new Entity("mocd_documentfile");   // NO Id set — CRM generates one
documentFile["mocd_name"]          = FileName;
documentFile["mocd_fileid"]        = documentResponse.data.fileId;   // vendor id, separate field
documentFile["mocd_category"]      = documentResponse.data.category;
documentFile["mocd_filename"]      = documentResponse.data.fileName;
documentFile["mocd_mediatype"]     = documentResponse.data.mediaType;
documentFile["mocd_extension"]     = documentResponse.data.extension;
documentFile["mocd_applicationid"] = documentResponse.data.applicationId;
documentFile["mocd_filepath"]      = documentResponse.data.filePath;
documentFile["mocd_filesize"]      = fileSize;
documentFile["mocd_hash"]          = documentResponse.data.hash;
Guid documentFileId = service.Create(documentFile);
```

Ten fields, and `applicationId` is the **document's own id**, passed in at the call site.

Its Category comes from only three lookups — `mocd_marriagegrant`, `mocd_podcardrequest`,
`mocd_podcentersrequest`. **None of the seven in-scope services is among them**, so `category`
stays `""` and the vendor files the document with no folder at all.

---

## 2. What the data shows

**Pre-prod, 5000 records that have a file path:**

| | count |
|---|---:|
| path stem **equals** `mocd_documentfileid` | 4671 |
| path stem is a **different** GUID | 329 |
| path stem is not a GUID | 0 |

Of the 329 that differ: **323 have no folder segment** in the path, and **315 have a
CRM-sequential id**. That is path B.

**Dev, the 380 in-scope documents that have a path:**

| group | rows | id == stem | id differs |
|---|---:|---:|---:|
| 1 path holds a document type id | 59 | 59 | 0 |
| 2 path holds `docType` | 57 | 57 | 0 |
| 3 path holds a name | 133 | 133 | 0 |
| **4 path has no catalogue segment** | **23** | **0** | **23** |
| 5 real but different catalogue | 41 | 41 | 0 |
| 6 needs a decision | 7 | 7 | 0 |
| 7 nothing to do | 60 | 60 | 0 |

**Group 4 is path B.** The correlation is exact, in both environments.

Two real records, side by side:

```
path B (group 4)                              path A (group 1)
  mocd_documentfileid 2a1c51a3-e330-f111-…      b18f901e-0805-4f22-96e7-6ed8ae38f1c0
  mocd_fileid         35687738-986f-413d-…      null
  mocd_filename       35687738-….png            null
  mocd_extension      png            (no dot)   null
  mocd_applicationid  c0c2a661-…  (document id) null
  mocd_filesize       4684                      null
  mocd_category       null                      9b1121f4-e30b-f111-b117-005056010908
  mocd_name           Screenshot ….png          Application Summary.png
  mocd_hash           021a49b9…                 5e7ba19d…
  mocd_filepath       DigitalServices\20260405\… DigitalServices\9b1121f4-…\20260624\…
```

---

## 3. What this means for the remediation tool

`CrmWriteClient.CreateDocumentFileAsync` writes **path A's six fields only**. For the 23 group-4
documents that is wrong in four ways:

1. **Data loss.** `mocd_fileid`, `mocd_filename`, `mocd_extension`, `mocd_applicationid` and
   `mocd_filesize` were populated on the old record and would be absent on the new one.
2. **The id convention flips.** A path-B document would come back as a path-A record: its id
   would become the vendor FileId, and `mocd_fileid` would be empty. Anything that reads
   `mocd_fileid` would stop finding it.
3. **`mocd_extension` differs in form.** Path B stores `png`; the tool holds `.png`.
4. **`mocd_applicationid` is the document id** on path B; the tool uploads `Guid.Empty`.

There is also a smaller consequence for **finding** documents: `ResolveIdentifierAsync` matches a
typed file stem against `mocd_documentfileid`, which cannot work for a path-B document. It should
also try `mocd_fileid`.

---

## 4. The fix: copy the old record forward

Rather than writing a fixed set of fields, the new `mocd_documentfile` should be **the old record
with only what must change replaced**. The complete old record is already captured in the backup,
in `old\crm.json`, before anything is written — so this needs no extra read.

**Replace:**

| field | new value |
|---|---|
| `mocd_filepath` | the new path from the upload |
| `mocd_hash` | the new vendor hash |
| `mocd_category` | the correct service catalogue id |
| `mocd_fileid` | the new vendor FileId — **only if the old record had it** |
| `mocd_filename` | the new vendor file name — **only if the old record had it** |

**Keep exactly as they were:** `mocd_name`, `mocd_mediatype`, `mocd_extension`, `mocd_filesize`,
`mocd_applicationid`, and anything else the old record carried that we have not thought of.

**Keep the id convention:** if the old record's id equalled its path stem (path A), set the new
record's id to the new vendor FileId. If it did not (path B), let CRM generate the id and put the
vendor id in `mocd_fileid`, exactly as the plugin does.

**Upload with the same inputs the original used:** `ApplicationId` should be the document id for a
path-B document, `Guid.Empty` for a path-A one, and `Extension` should keep the form the old
record used.

The principle: a migrated record should be indistinguishable from one the original code would
have produced today, except that its path is right.

---

## 5. Still to raise separately

`UploadDocument.cs` derives `category` from three lookups, none of them in scope, so every
CRM-side upload for the seven services still lands with no folder. This tool cleans up after that
defect; it does not fix it. Line 37 also reads `"employeeappintmentrequest"` without the `mocd_`
prefix, so that column is never returned — and is unused in any case.
