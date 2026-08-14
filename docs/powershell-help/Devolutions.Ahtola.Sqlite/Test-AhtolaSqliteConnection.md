---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Test-AhtolaSqliteConnection
---

# Test-AhtolaSqliteConnection

## SYNOPSIS

Tests an Ahtola local or Turso Cloud connection with `SELECT 1`.

## SYNTAX

### __AllParameterSets

```
Test-AhtolaSqliteConnection -Connection <DbConnection>
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Opens the supplied connection if needed, executes `SELECT 1`, and writes `$true` on success. The connection remains caller-owned and open.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Test-AhtolaSqliteConnection -Connection $connection
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: System.Data.Common.DbConnection
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

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.Data.Common.DbConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Boolean

`$true` when the test query succeeds.

## NOTES

The cmdlet throws on failure; it does not return `$false`.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
