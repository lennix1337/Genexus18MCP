---
name: object-file
description: Stores external resources within Knowledge Base
---

Stores external resources (JSON, XML, TXT, etc.) within Knowledge Base

---

# DEFINITION
A `File` object stores external resources within the Knowledge Base for information exchange or deployment packaging

---

# SYNTAX
~~~
File <name>
{
	<content>

	#Properties
		<properties>
	#End

	#Documentation
		<documentation>
	#End
}
~~~

Where:
- `<name>`: Object name using alphanumeric or underscore, starting with letter
- `<content>`: File object content in UTF-8 encoding (plain, JSON, XML, INI, etc) or Base64 encoded
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-file.md)
- `<documentation>`: Optional object documentation; see [markdown](./common-markdown.md)

Rules:
- Omit `<content>` when sibling file with `<name>` filename exists

---

# OUTPUT
Use [global-output](./global-output.md) with:
- Location: `#files/`

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Name must include file extension in lowercase
- Text files only (.txt, .json, .xml, .ini, .bat, .sh, .cs, .java, etc.)
- Binary files (.mp3, .wav, .ttf, .otf, .dll, .exe, etc.) read-only, never output binary content
- Avoid leading or trailing tabs/whitespace unless required by format
- Use UTF-8 encoding
- Store `File` objects only in module `#files/` directories
- Files cannot be specified under folders, only modules

---

# EXAMPLES

## Example 1
API Key File
~~~
File ApiKeyFile.txt
{
	REDACTED_API_KEY

	#Properties
		Description = "Service API Key"
	#End
}
~~~

## Example 2
JSON Configuration File
~~~
File AppSettingsFile.json
{
	{
		"apiKey": "REDACTED_API_KEY",
		"baseUrl": "https://api.example.com/v1/",
		"timeout": 30,
		"retries": 3,
		"logging": {
			"enabled": true,
			"level": "info"
		}
	}

	#Properties
		Description = "Appliction settings file"
	#End
}
~~~

## Example 3
XML Configuration File
~~~
File AppSettingsFile.xml
{
	<?xml version="1.0" encoding="UTF-8"?>
	<Settings description="Configuration file">
		<ApiKey>REDACTED_API_KEY</ApiKey>
		<BaseUrl>https://api.example.com/v1/</BaseUrl>
		<Timeout>30</Timeout>
		<Retries>3</Retries>
		<Logging>
			<Enabled>true</Enabled>
			<Level>info</Level>
		</Logging>
	</Settings>

	#Properties
		Description = "Application settings file"
	#End
}
~~~

## Example 4
INI Configuration File
~~~
File AppConfig.ini
{
	[Database]
	Server=localhost
	Port=5432
	Database=mydb
	Username=admin

	[Logging]
	Level=INFO
	FilePath=./logs/app.log

	#Properties
		Description = "Application configuration file"
	#End
}
~~~
