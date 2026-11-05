//! GXWebObjectStub and GXWebPanel C# class generator.

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::{
        emitter::CSharpEmitter,
        procedure::CsharpParam,
        types::{
            clean_ident, to_csharp_default_value, to_csharp_param_name, to_csharp_param_signature,
            to_csharp_type, to_csharp_var_name,
        },
    },
    model::{DataType, ParamMode},
    webpanel::{ControlKind, EventKind, LayoutControl, ScreenVariable, WebPanel, WebPanelEvent},
};
use std::collections::BTreeMap;
use std::fmt::Write;

/// Extended imports for WebPanel C# classes.
pub use crate::csharp::types::EXTENDED_DATA_IMPORTS as WEBPANEL_IMPORTS;

/// Flattens a hierarchy of layout controls into a flat list.
fn collect_all_controls<'a>(controls: &'a [LayoutControl], out: &mut Vec<&'a LayoutControl>) {
    for c in controls {
        out.push(c);
        collect_all_controls(&c.children, out);
    }
}

fn skip_quoted_prefix(rest: &str, quote: char) -> &str {
    let mut escaped = false;
    for (idx, ch) in rest.char_indices().skip(1) {
        if escaped {
            escaped = false;
        } else if ch == '\\' {
            escaped = true;
        } else if ch == quote {
            return &rest[idx + ch.len_utf8()..];
        }
    }
    ""
}

fn has_unterminated_block_comment(mut rest: &str) -> bool {
    while !rest.is_empty() {
        if let Some(after_line) = rest.strip_prefix("//") {
            rest = after_line.find('\n').map_or("", |nl| &after_line[nl + 1..]);
        } else if let Some(after_block) = rest.strip_prefix("/*") {
            let Some(end) = after_block.find("*/") else {
                return true;
            };
            rest = &after_block[end + 2..];
        } else if rest.starts_with('\'') {
            rest = skip_quoted_prefix(rest, '\'');
        } else if rest.starts_with('"') {
            rest = skip_quoted_prefix(rest, '"');
        } else {
            let step = rest.chars().next().map_or(1, char::len_utf8);
            rest = &rest[step..];
        }
    }
    false
}

fn validate_plain_events_source(object_name: &str, source: &str) -> Result<(), String> {
    if has_unterminated_block_comment(source) {
        return Err(format!(
            "events of '{object_name}' contain an unterminated block comment"
        ));
    }
    let clean = crate::dataview::strip_comments(source);
    let trimmed = clean.trim();
    let event_body = if let Some(rest) = trimmed.strip_prefix("events") {
        let rest = rest.trim();
        rest.strip_prefix('{')
            .and_then(|inner| inner.strip_suffix('}'))
            .ok_or_else(|| {
                format!("events of '{object_name}' have malformed 'events {{ ... }}' block")
            })?
    } else {
        trimmed
    };

    let mut in_event = false;
    let mut event_count = 0usize;
    for line in event_body.lines() {
        let l = line.trim();
        if l.is_empty() {
            continue;
        }
        if !in_event {
            if let Some(rest) = l.strip_prefix("Event ") {
                let event_name = rest.trim().trim_matches('\'').trim_matches('"').trim();
                if event_name.is_empty() {
                    return Err(format!(
                        "events of '{object_name}' contain an Event declaration without a name"
                    ));
                }
                in_event = true;
                event_count += 1;
            } else {
                return Err(format!(
                    "events of '{object_name}' contain unexpected text outside Event...EndEvent: '{l}'"
                ));
            }
        } else if l.eq_ignore_ascii_case("EndEvent") {
            in_event = false;
        } else if l.starts_with("Event ") {
            return Err(format!(
                "events of '{object_name}' contain a nested or unclosed Event before '{l}'"
            ));
        }
    }

    if in_event {
        return Err(format!(
            "events of '{object_name}' contain an unclosed Event block (missing EndEvent)"
        ));
    }
    if event_count == 0 {
        return Err(format!(
            "events of '{object_name}' contain no Event...EndEvent blocks"
        ));
    }

    Ok(())
}

fn parse_events_part(object_name: &str, source: &str) -> Result<Vec<WebPanelEvent>, String> {
    if !super::webform::has_non_comment_code(source) {
        return Ok(Vec::new());
    }

    let events = if let Ok(events) = serde_json::from_str::<Vec<WebPanelEvent>>(source) {
        events
    } else {
        validate_plain_events_source(object_name, source)?;
        let parsed = match WebPanel::parse(object_name, source) {
            Ok(wp) if !wp.events.is_empty() => wp,
            _ => WebPanel::parse(object_name, &format!("events {{\n{source}\n}}"))
                .map_err(|e| format!("events of '{object_name}': {e}"))?,
        };
        if parsed.events.is_empty() {
            return Err(format!(
                "events of '{object_name}' contain non-comment text that parsed to zero events"
            ));
        }
        parsed.events
    };

    for ev in &events {
        if ev.name.trim().is_empty() {
            return Err(format!(
                "events of '{object_name}' contain an event with an empty name"
            ));
        }
        if super::webform::has_non_comment_code(&ev.body) {
            crate::frontend::parse_body(ev.body.trim())
                .map_err(|e| format!("events of '{object_name}' ({}): {e}", ev.name))?;
        }
    }

    Ok(events)
}

fn parse_variables_part(object_name: &str, source: &str) -> Result<Vec<ScreenVariable>, String> {
    if source.trim().is_empty() {
        return Ok(Vec::new());
    }
    if let Ok(vars) = serde_json::from_str::<Vec<ScreenVariable>>(source) {
        return Ok(vars);
    }
    if let Ok(vars_map) = serde_json::from_str::<BTreeMap<String, DataType>>(source) {
        return Ok(vars_map
            .into_iter()
            .map(|(k, v)| ScreenVariable::new(k, v))
            .collect());
    }
    Err(format!(
        "variables of '{object_name}' are not valid JSON ScreenVariable list or DataType map"
    ))
}

fn apply_webpanel_part(
    wp: &mut WebPanel,
    object_name: &str,
    part: &crate::catalog::SourcePart,
) -> Result<(), String> {
    match part.name.as_str() {
        "VARIABLES" => {
            wp.variables = parse_variables_part(object_name, &part.source)?;
        }
        "EVENTS" => {
            wp.events = parse_events_part(object_name, &part.source)?;
        }
        "LAYOUT" if !part.source.trim().is_empty() => {
            wp.controls =
                serde_json::from_str::<Vec<LayoutControl>>(&part.source).map_err(|e| {
                    format!("layout of '{object_name}' is not valid JSON LayoutControl list: {e}")
                })?;
        }
        "RULES" => {
            crate::rules::parse_rules(&part.source)
                .map_err(|e| format!("rules of '{object_name}': {e}"))?;
            if !part.source.trim().is_empty() {
                wp.rules.push(part.source.clone());
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_webpanel_rules(object_name: &str, wp: &WebPanel) -> Result<(), String> {
    wp.rules.iter().try_for_each(|rule_str| {
        crate::rules::parse_rules(rule_str)
            .map(|_| ())
            .map_err(|e| format!("rules of '{object_name}': {e}"))
    })?;
    wp.validate()
        .map_err(|e| format!("webpanel '{object_name}': {e}"))
}

/// Normalizes a UI control name to GeneXus C# control field convention.
pub fn to_csharp_control_name(name: &str, kind: &ControlKind) -> String {
    let clean = clean_ident(name);
    match kind {
        ControlKind::TextBlock => {
            if clean.starts_with("lbl") {
                clean
            } else {
                format!("lbl{clean}")
            }
        }
        ControlKind::Button => {
            if let Some(rest) = clean.strip_prefix("btn") {
                format!("btt{rest}")
            } else if clean.starts_with("btt") {
                clean
            } else {
                format!("btt{clean}")
            }
        }
        ControlKind::Grid => {
            if clean.starts_with("Grid") {
                clean
            } else {
                format!("Grid{clean}")
            }
        }
        ControlKind::Table | ControlKind::ResponsiveTable => {
            if clean.starts_with("tbl") {
                clean
            } else {
                format!("tbl{clean}")
            }
        }
        ControlKind::Edit => {
            if clean.starts_with("edt") {
                clean
            } else {
                format!("edt{clean}")
            }
        }
        _ => clean,
    }
}

/// Represents an emittable GeneXus C# WebPanel class (`GXWebPanel` / `GXWebObjectStub`).
#[derive(Debug, Clone)]
pub struct WebPanelClass {
    pub name: String,
    pub namespace: String,
    pub theme: Option<String>,
    pub master_page: Option<String>,
    pub base_class: String,
    pub is_main: bool,
    pub params: Vec<CsharpParam>,
    pub controls: Vec<LayoutControl>,
    pub events: Vec<WebPanelEvent>,
    pub variables: BTreeMap<String, DataType>,
    pub rules: Vec<String>,
}

impl WebPanelClass {
    /// Creates a new empty WebPanelClass with default settings.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            namespace: "GeneXus.Programs".to_string(),
            theme: Some("KBTeste".to_string()),
            master_page: None,
            base_class: "GXWebPanel".to_string(),
            is_main: false,
            params: Vec::new(),
            controls: Vec::new(),
            events: Vec::new(),
            variables: BTreeMap::new(),
            rules: Vec::new(),
        }
    }

    /// Sets the application theme.
    pub fn with_theme(mut self, theme: impl Into<String>) -> Self {
        self.theme = Some(theme.into());
        self
    }

    /// Sets the master page name.
    pub fn with_master_page(mut self, master_page: impl Into<String>) -> Self {
        self.master_page = Some(master_page.into());
        self
    }

    /// Sets the base class to inherit from (`GXWebPanel`, `GXWebObjectStub`, or `GXDataArea`).
    pub fn with_base_class(mut self, base_class: impl Into<String>) -> Self {
        self.base_class = base_class.into();
        self
    }

    /// Sets custom namespace.
    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    /// Sets whether this WebPanel is an entry point.
    pub fn with_is_main(mut self, is_main: bool) -> Self {
        self.is_main = is_main;
        self
    }

    /// Adds a typed parameter.
    pub fn add_param(mut self, param: CsharpParam) -> Self {
        self.params.push(param);
        self
    }

    /// Adds a layout control.
    pub fn add_control(mut self, control: LayoutControl) -> Self {
        self.controls.push(control);
        self
    }

    /// Adds an authored event block.
    pub fn add_event(mut self, event: WebPanelEvent) -> Self {
        self.events.push(event);
        self
    }

    /// Adds a declared screen variable.
    pub fn add_variable(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.variables.insert(name.into(), data_type);
        self
    }

    /// Constructs a WebPanelClass from a portable `WebPanel` AST.
    pub fn from_webpanel(panel: &WebPanel) -> Self {
        let mut vars = BTreeMap::new();
        for v in &panel.variables {
            let key = v.name.trim_start_matches('&').to_string();
            vars.insert(key, v.data_type.clone());
        }

        let mut params = Vec::new();
        for rule_str in &panel.rules {
            if let Ok(rules) = crate::rules::parse_rules(rule_str) {
                for rule in rules {
                    if let crate::rules::Rule::Parm(args) = rule {
                        for (i, arg) in args.iter().enumerate() {
                            let (mode, param_ident) = if arg.mode.is_empty() {
                                (ParamMode::InOut, arg.name.as_str())
                            } else {
                                let mode = match arg.mode.to_ascii_lowercase().as_str() {
                                    "in" => ParamMode::In,
                                    "out" => ParamMode::Out,
                                    _ => ParamMode::InOut,
                                };
                                (mode, arg.name.as_str())
                            };

                            let lookup_key = param_ident.trim_start_matches('&');
                            let fallback_name = clean_ident(param_ident);
                            let (matched_name, data_type) = vars
                                .iter()
                                .find(|(k, _)| {
                                    k.eq_ignore_ascii_case(lookup_key)
                                        || k.eq_ignore_ascii_case(param_ident)
                                })
                                .map(|(k, v)| (k.clone(), v.clone()))
                                .unwrap_or_else(|| {
                                    (
                                        fallback_name,
                                        DataType::Numeric {
                                            precision: 10,
                                            scale: 0,
                                        },
                                    )
                                });

                            let clean = clean_ident(&matched_name);
                            let param_name = to_csharp_param_name(i, &matched_name);
                            let var_name = to_csharp_var_name(&matched_name);
                            params.push(CsharpParam {
                                name: clean,
                                data_type,
                                mode,
                                param_name,
                                var_name,
                            });
                        }
                    }
                }
            }
        }

        Self {
            name: panel.name.clone(),
            namespace: "GeneXus.Programs".to_string(),
            theme: panel.theme.clone().or_else(|| Some("KBTeste".to_string())),
            master_page: panel.master_page.clone(),
            base_class: "GXWebPanel".to_string(),
            is_main: false,
            params,
            controls: panel.controls.clone(),
            events: panel.events.clone(),
            variables: vars,
            rules: panel.rules.clone(),
        }
    }

    /// Constructs a WebPanelClass directly from a portable `KbObject`.
    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        if object.kind != ObjectKind::WebPanel {
            return Err(format!("expected WebPanel, found {:?}", object.kind));
        }

        for part in &object.source_parts {
            if matches!(part.name.as_str(), "WEBPANEL" | "SOURCE") {
                let wp = WebPanel::parse(&object.name, &part.source)
                    .map_err(|e| format!("{} of '{}': {e}", part.name, object.name))?;
                validate_webpanel_rules(&object.name, &wp)?;
                return Ok(Self::from_webpanel(&wp));
            }
        }

        let mut wp = WebPanel {
            name: object.name.clone(),
            ..Default::default()
        };

        for part in &object.source_parts {
            apply_webpanel_part(&mut wp, &object.name, part)?;
        }

        validate_webpanel_rules(&object.name, &wp)?;

        Ok(Self::from_webpanel(&wp))
    }

    fn emit_usings(&self, out: &mut String) {
        for imp in WEBPANEL_IMPORTS {
            let _ = writeln!(out, "using {imp};");
        }
    }

    fn emit_constructors(&self, out: &mut String) {
        let _ = writeln!(out, "      public {}( )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         context = new GxContext(  );");
        let _ = writeln!(out, "         DataStoreUtil.LoadDataStores( context);");
        let _ = writeln!(out, "         IsMain = true;");
        if let Some(theme) = &self.theme {
            let _ = writeln!(out, "         context.SetDefaultTheme(\"{theme}\", true);");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public {}( IGxContext context )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "         IsMain = false;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_execute_method(&self, out: &mut String) {
        let param_signatures = if self.params.is_empty() {
            String::new()
        } else {
            let sigs: Vec<String> = self
                .params
                .iter()
                .map(|p| to_csharp_param_signature(p.mode, &p.data_type, &p.param_name))
                .collect();
            format!(" {} ", sigs.join(" ,\n                           "))
        };

        let _ = writeln!(out, "      public void execute({param_signatures})");
        let _ = writeln!(out, "      {{");
        for p in &self.params {
            match p.mode {
                ParamMode::In | ParamMode::InOut => {
                    let _ = writeln!(out, "         this.{} = {};", p.var_name, p.param_name);
                }
                ParamMode::Out => {
                    let default_val = to_csharp_default_value(&p.data_type);
                    let _ = writeln!(out, "         this.{} = {} ;", p.var_name, default_val);
                }
            }
        }
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         ExecuteImpl();");
        for p in &self.params {
            match p.mode {
                ParamMode::Out | ParamMode::InOut => {
                    let _ = writeln!(out, "         {} = this.{};", p.param_name, p.var_name);
                }
                ParamMode::In => {}
            }
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_execution_lifecycle(&self, out: &mut String) {
        let _ = writeln!(out, "      protected override void ExecutePrivate( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         isStatic = false;");
        let _ = writeln!(out, "         webExecute();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void webExecute( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         createObjects();");
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         INITWEB();");
        let _ = writeln!(out, "         if ( ! isAjaxCallMode( ) )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            PA();");
        let _ = writeln!(out, "            Refresh();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void INITWEB( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         initialize_properties();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void PA( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_create_objects(&self, flat_controls: &[&LayoutControl], out: &mut String) {
        let _ = writeln!(out, "      protected override void createObjects( )");
        let _ = writeln!(out, "      {{");
        for c in flat_controls {
            let ctrl_name = to_csharp_control_name(&c.name, &c.kind);
            match c.kind {
                ControlKind::Button => {
                    let _ = writeln!(out, "         {ctrl_name} = new GXButton();");
                }
                ControlKind::Grid => {
                    let _ = writeln!(out, "         {ctrl_name} = new GXGrid();");
                }
                ControlKind::TextBlock => {
                    let _ = writeln!(out, "         {ctrl_name} = new GXTextBlock();");
                }
                ControlKind::Table | ControlKind::ResponsiveTable => {
                    let _ = writeln!(out, "         {ctrl_name} = new GXTable();");
                }
                ControlKind::Edit => {
                    let _ = writeln!(out, "         {ctrl_name} = new GXEdit();");
                }
                _ => {}
            }
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_cleanup_and_init(&self, flat_controls: &[&LayoutControl], out: &mut String) {
        let _ = writeln!(out, "      public override void cleanup( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         CloseCursors();");
        let _ = writeln!(out, "         if ( IsMain )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            context.CloseConnections();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public override void initialize( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas. */");
        for (var_name, data_type) in &self.variables {
            let cs_name = to_csharp_var_name(var_name);
            let default_val = to_csharp_default_value(data_type);
            let _ = writeln!(out, "         {cs_name} = {default_val} ;");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public void initialize_properties( )");
        let _ = writeln!(out, "      {{");
        for c in flat_controls {
            let ctrl_name = to_csharp_control_name(&c.name, &c.kind);
            if let Some(caption) = &c.caption {
                match c.kind {
                    ControlKind::TextBlock | ControlKind::Button => {
                        let _ = writeln!(out, "         {ctrl_name}.Caption = \"{caption}\";");
                    }
                    _ => {}
                }
            }
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_user_events(&self, out: &mut String) {
        let _ = writeln!(
            out,
            "      public void ExecuteUserEvent( string eventName )"
        );
        let _ = writeln!(out, "      {{");
        let _ = writeln!(
            out,
            "         string upperEvt = StringUtil.Upper( StringUtil.Trim( eventName ) );"
        );

        let mut first = true;
        for ev in &self.events {
            let clean = clean_ident(&ev.name);
            let ev_upper = ev
                .name
                .trim()
                .trim_matches('\'')
                .trim_matches('"')
                .to_ascii_uppercase();
            let if_stmt = if first {
                first = false;
                "if"
            } else {
                "else if"
            };

            let method_call = match ev.event_kind() {
                EventKind::Start => "E12_Start();".to_string(),
                EventKind::Refresh => "E11_Refresh();".to_string(),
                EventKind::Enter => "E13_Enter();".to_string(),
                EventKind::Load { grid } => {
                    if let Some(g) = grid {
                        format!("E13_{}_Load();", clean_ident(&g))
                    } else {
                        "E13_Load();".to_string()
                    }
                }
                EventKind::User(_) => format!("E14_{clean}();"),
            };

            let _ = writeln!(
                out,
                "         {if_stmt} ( StringUtil.StrCmp(upperEvt, \"{ev_upper}\") == 0 )"
            );
            let _ = writeln!(out, "         {{");
            let _ = writeln!(out, "            {method_call}");
            let _ = writeln!(out, "         }}");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        for ev in &self.events {
            let clean = clean_ident(&ev.name);
            let method_name = match ev.event_kind() {
                EventKind::Start => "E12_Start".to_string(),
                EventKind::Refresh => "E11_Refresh".to_string(),
                EventKind::Enter => "E13_Enter".to_string(),
                EventKind::Load { grid } => {
                    if let Some(g) = grid {
                        format!("E13_{}_Load", clean_ident(&g))
                    } else {
                        "E13_Load".to_string()
                    }
                }
                EventKind::User(_) => format!("E14_{clean}"),
            };

            let _ = writeln!(out, "      protected void {method_name}( )");
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         /* {} Event */", ev.name);

            let trimmed_body = ev.body.trim();
            if !trimmed_body.is_empty() {
                if let Ok(stmts) = crate::frontend::parse_body(trimmed_body) {
                    let mut emitter = CSharpEmitter::with_vars(&self.variables);
                    emitter.indented(|e| {
                        e.indented(|e| {
                            e.indented(|e| {
                                for stmt in &stmts {
                                    out.push_str(&e.emit_stmt(stmt, &self.variables));
                                }
                            });
                        });
                    });
                } else {
                    for line in trimmed_body.lines() {
                        let l = line.trim();
                        if !l.is_empty() {
                            let _ = writeln!(out, "         /* {l} */");
                        }
                    }
                }
            }

            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_fields(&self, flat_controls: &[&LayoutControl], out: &mut String) {
        for c in flat_controls {
            let ctrl_name = to_csharp_control_name(&c.name, &c.kind);
            match c.kind {
                ControlKind::Button => {
                    let _ = writeln!(out, "      private GXButton {ctrl_name} ;");
                }
                ControlKind::Grid => {
                    let _ = writeln!(out, "      private GXGrid {ctrl_name} ;");
                }
                ControlKind::TextBlock => {
                    let _ = writeln!(out, "      private GXTextBlock {ctrl_name} ;");
                }
                ControlKind::Table | ControlKind::ResponsiveTable => {
                    let _ = writeln!(out, "      private GXTable {ctrl_name} ;");
                }
                ControlKind::Edit => {
                    let _ = writeln!(out, "      private GXEdit {ctrl_name} ;");
                }
                _ => {}
            }
        }

        for (var_name, data_type) in &self.variables {
            let cs_type = to_csharp_type(data_type);
            let cs_name = to_csharp_var_name(var_name);
            let _ = writeln!(out, "      private {cs_type} {cs_name} ;");
        }

        for p in &self.params {
            let cs_type = to_csharp_type(&p.data_type);
            let _ = writeln!(out, "      private {cs_type} {} ;", p.param_name);
        }
    }

    /// Emits complete C# source code (.cs) for this WebPanel.
    pub fn emit(&self) -> String {
        let mut out = String::with_capacity(4096);

        self.emit_usings(&mut out);
        let _ = writeln!(out, "namespace {} {{", self.namespace);
        let _ = writeln!(out, "   public class {} : {}", self.name, self.base_class);
        let _ = writeln!(out, "   {{");

        let mut flat_controls = Vec::new();
        collect_all_controls(&self.controls, &mut flat_controls);

        self.emit_constructors(&mut out);
        self.emit_execute_method(&mut out);
        self.emit_execution_lifecycle(&mut out);
        self.emit_create_objects(&flat_controls, &mut out);
        self.emit_cleanup_and_init(&flat_controls, &mut out);
        self.emit_user_events(&mut out);
        self.emit_fields(&flat_controls, &mut out);

        let _ = writeln!(out, "   }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "}}");

        out
    }
}
