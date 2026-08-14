---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Get-AhtolaSqliteDatabaseMetadata
---

# Get-AhtolaSqliteDatabaseMetadata

## SYNOPSIS

Retrieves stored Ahtola SQLite metadata values.

## SYNTAX

### __AllParameterSets

```
Get-AhtolaSqliteDatabaseMetadata -Connection <SqliteConnection> [-MetadataKey <string[]>]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Reads metadata from the supplied local connection. Specify keys, or omit `MetadataKey` to request all values using the default `*`.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Get-AhtolaSqliteDatabaseMetadata -Connection $connection
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteConnection
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -MetadataKey

One or more metadata keys. The default `*` requests all stored metadata.

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### Ahtola.Data.Sqlite.SqliteConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Collections.IDictionary

A dictionary of metadata keys and values when metadata is present.

## NOTES

The supplied connection is caller-owned and is not closed.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
