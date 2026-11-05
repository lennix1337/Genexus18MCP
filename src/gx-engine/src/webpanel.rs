//! WebPanel, Layout, and Event Engine (Rows B4, J1, Plan 130 Track B).
//!
//! Provides data structures, DSL grammar parser, layout control tree,
//! screen variables, rules, and event handler classification for GeneXus
//! WebPanels, forms, and UI event logic.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;

use crate::dataview::strip_comments;
use crate::model::DataType;

/// Types of UI controls supported in WebPanel forms and layouts.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ControlKind {
    TextBlock,
    Attribute,
    Variable,
    Button,
    Grid,
    Table,
    ResponsiveTable,
    Edit,
    Canvas,
    UserControl,
}

/// A single UI control in a WebPanel form layout tree.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LayoutControl {
    pub name: String,
    pub kind: ControlKind,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub caption: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub binding: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub event_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme_class: Option<String>,
    #[serde(default)]
    pub is_read_only: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub children: Vec<LayoutControl>,
}

impl LayoutControl {
    pub fn textblock(name: impl Into<String>, caption: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::TextBlock,
            caption: Some(caption.into()),
            binding: None,
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children: Vec::new(),
        }
    }

    pub fn button(
        name: impl Into<String>,
        caption: impl Into<String>,
        event_name: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::Button,
            caption: Some(caption.into()),
            binding: None,
            event_name: Some(event_name.into()),
            theme_class: None,
            is_read_only: false,
            children: Vec::new(),
        }
    }

    pub fn attribute(name: impl Into<String>, binding: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::Attribute,
            caption: None,
            binding: Some(binding.into()),
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children: Vec::new(),
        }
    }

    pub fn edit(name: impl Into<String>, binding: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::Edit,
            caption: None,
            binding: Some(binding.into()),
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children: Vec::new(),
        }
    }

    pub fn grid(
        name: impl Into<String>,
        binding: Option<String>,
        children: Vec<LayoutControl>,
    ) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::Grid,
            caption: None,
            binding,
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children,
        }
    }

    pub fn table(name: impl Into<String>, children: Vec<LayoutControl>) -> Self {
        Self {
            name: name.into(),
            kind: ControlKind::Table,
            caption: None,
            binding: None,
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children,
        }
    }
}

/// Screen variable declared in a WebPanel.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ScreenVariable {
    pub name: String,
    pub data_type: DataType,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub is_collection: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub initial_value: Option<String>,
}

impl ScreenVariable {
    pub fn new(name: impl Into<String>, data_type: DataType) -> Self {
        Self {
            name: name.into(),
            data_type,
            description: None,
            is_collection: false,
            initial_value: None,
        }
    }

    pub fn validate(&self) -> Result<(), String> {
        let norm = self.name.trim();
        if norm.is_empty() {
            return Err("ScreenVariable name cannot be empty".to_string());
        }
        let check_name = norm.strip_prefix('&').unwrap_or(norm);
        if check_name.is_empty()
            || !check_name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '_')
        {
            return Err(format!("Invalid ScreenVariable identifier '{}'", self.name));
        }
        crate::model::validate_data_type(&self.data_type)?;
        crate::model::require_attribute_form(&self.data_type)?;
        Ok(())
    }
}

/// Category / Lifecycle of a WebPanel Event.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EventKind {
    Start,
    Refresh,
    Enter,
    Load {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        grid: Option<String>,
    },
    User(String),
}

/// An authored Event block in a WebPanel (e.g. Start, Refresh, Enter, Grid.Load).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct WebPanelEvent {
    pub name: String,
    pub body: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<EventKind>,
}

impl WebPanelEvent {
    pub fn new(name: impl Into<String>, body: impl Into<String>) -> Self {
        let name_str = name.into();
        let kind = Self::classify_event_kind(&name_str);
        Self {
            name: name_str,
            body: body.into(),
            kind: Some(kind),
        }
    }

    pub fn event_kind(&self) -> EventKind {
        if let Some(ref k) = self.kind {
            k.clone()
        } else {
            Self::classify_event_kind(&self.name)
        }
    }

    pub fn is_start(&self) -> bool {
        matches!(self.event_kind(), EventKind::Start)
    }

    pub fn is_refresh(&self) -> bool {
        matches!(self.event_kind(), EventKind::Refresh)
    }

    pub fn is_enter(&self) -> bool {
        matches!(self.event_kind(), EventKind::Enter)
    }

    pub fn is_load(&self) -> bool {
        matches!(self.event_kind(), EventKind::Load { .. })
    }

    pub fn is_user(&self) -> bool {
        matches!(self.event_kind(), EventKind::User(_))
    }

    pub fn classify_event_kind(name: &str) -> EventKind {
        let clean = name.trim().trim_matches('\'').trim_matches('"');
        let lower = clean.to_ascii_lowercase();
        if lower == "start" {
            EventKind::Start
        } else if lower == "refresh" {
            EventKind::Refresh
        } else if lower == "enter" {
            EventKind::Enter
        } else if lower == "load" {
            EventKind::Load { grid: None }
        } else if let Some((grid, ev)) = clean.split_once('.') {
            if ev.trim().eq_ignore_ascii_case("load") {
                EventKind::Load {
                    grid: Some(grid.trim().to_string()),
                }
            } else {
                EventKind::User(clean.to_string())
            }
        } else {
            EventKind::User(clean.to_string())
        }
    }
}

/// Complete WebPanel definition with Form Layout, Event logic, Rules, and Screen Variables.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct WebPanel {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub master_page: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    #[serde(default)]
    pub controls: Vec<LayoutControl>,
    #[serde(default)]
    pub events: Vec<WebPanelEvent>,
    #[serde(default)]
    pub rules: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub variables: Vec<ScreenVariable>,
}

impl WebPanel {
    /// Parses a WebPanel from either JSON or structured DSL format.
    pub fn parse(name: &str, source: &str) -> Result<Self, String> {
        let clean = strip_comments(source);
        let trimmed = clean.trim();

        if trimmed.is_empty() {
            return Err("WebPanel source cannot be empty".to_string());
        }

        // Support JSON serialization directly
        if trimmed.starts_with('{') {
            let mut wp: WebPanel = serde_json::from_str(trimmed)
                .map_err(|e| format!("Failed to parse WebPanel JSON: {e}"))?;
            if wp.name.is_empty() && !name.is_empty() {
                wp.name = name.to_string();
            }
            wp.validate()?;
            return Ok(wp);
        }

        // Parse DSL format:
        // webpanel <Name> {
        //   master_page = ...;
        //   theme = ...;
        //   variables { ... }
        //   layout { ... }
        //   rules { ... }
        //   events { ... }
        // }
        let mut wp = WebPanel {
            name: name.to_string(),
            ..Default::default()
        };

        let mut in_layout = false;
        let mut in_events = false;
        let mut in_rules = false;
        let mut in_vars = false;
        let mut layout_block = String::new();
        let mut events_block = String::new();
        let mut rules_block = String::new();
        let mut vars_block = String::new();

        let mut brace_depth = 0;

        for line in clean.lines() {
            let l = line.trim();
            if l.is_empty() {
                continue;
            }

            if l.starts_with("webpanel ") {
                let parts: Vec<&str> = l.split_whitespace().collect();
                if parts.len() >= 2 {
                    let parsed_name = parts[1].trim_matches('{').trim();
                    if !parsed_name.is_empty() {
                        wp.name = parsed_name.to_string();
                    }
                }
            } else if l.starts_with("master_page") && l.contains('=') {
                if let Some((_, val)) = l.split_once('=') {
                    wp.master_page =
                        Some(val.trim().trim_matches(';').trim_matches('"').to_string());
                }
            } else if l.starts_with("theme") && l.contains('=') {
                if let Some((_, val)) = l.split_once('=') {
                    wp.theme = Some(val.trim().trim_matches(';').trim_matches('"').to_string());
                }
            } else if l.starts_with("layout") && l.ends_with('{') {
                in_layout = true;
                brace_depth = 1;
            } else if l.starts_with("events") && l.ends_with('{') {
                in_events = true;
                brace_depth = 1;
            } else if l.starts_with("rules") && l.ends_with('{') {
                in_rules = true;
                brace_depth = 1;
            } else if l.starts_with("variables") && l.ends_with('{') {
                in_vars = true;
                brace_depth = 1;
            } else if in_layout {
                if l.contains('{') {
                    brace_depth += l.matches('{').count();
                }
                if l.contains('}') {
                    let closes = l.matches('}').count();
                    if closes >= brace_depth {
                        in_layout = false;
                        brace_depth = 0;
                        wp.controls = parse_layout_controls(&layout_block)?;
                        layout_block.clear();
                        continue;
                    } else {
                        brace_depth -= closes;
                    }
                }
                layout_block.push_str(line);
                layout_block.push('\n');
            } else if in_events {
                if l.contains('{') {
                    brace_depth += l.matches('{').count();
                }
                if l.contains('}') {
                    let closes = l.matches('}').count();
                    if closes >= brace_depth {
                        in_events = false;
                        brace_depth = 0;
                        wp.events = parse_events(&events_block)?;
                        events_block.clear();
                        continue;
                    } else {
                        brace_depth -= closes;
                    }
                }
                events_block.push_str(line);
                events_block.push('\n');
            } else if in_rules {
                if l.contains('{') {
                    brace_depth += l.matches('{').count();
                }
                if l.contains('}') {
                    let closes = l.matches('}').count();
                    if closes >= brace_depth {
                        in_rules = false;
                        brace_depth = 0;
                        wp.rules = parse_rules_block(&rules_block)?;
                        rules_block.clear();
                        continue;
                    } else {
                        brace_depth -= closes;
                    }
                }
                rules_block.push_str(line);
                rules_block.push('\n');
            } else if in_vars {
                if l.contains('{') {
                    brace_depth += l.matches('{').count();
                }
                if l.contains('}') {
                    let closes = l.matches('}').count();
                    if closes >= brace_depth {
                        in_vars = false;
                        brace_depth = 0;
                        wp.variables = parse_variables_block(&vars_block)?;
                        vars_block.clear();
                        continue;
                    } else {
                        brace_depth -= closes;
                    }
                }
                vars_block.push_str(line);
                vars_block.push('\n');
            }
        }

        if !layout_block.is_empty() {
            wp.controls = parse_layout_controls(&layout_block)?;
        }
        if !events_block.is_empty() {
            wp.events = parse_events(&events_block)?;
        }
        if !rules_block.is_empty() {
            wp.rules = parse_rules_block(&rules_block)?;
        }
        if !vars_block.is_empty() {
            wp.variables = parse_variables_block(&vars_block)?;
        }

        wp.validate()?;
        Ok(wp)
    }

    /// Validates the WebPanel structure fail-closed.
    pub fn validate(&self) -> Result<(), String> {
        if self.name.is_empty() {
            return Err("WebPanel name cannot be empty".to_string());
        }

        let mut control_names = HashSet::new();
        self.validate_controls(&self.controls, &mut control_names)?;

        let mut event_names = HashSet::new();
        let (mut start_count, mut refresh_count, mut enter_count) = (0usize, 0usize, 0usize);

        for ev in &self.events {
            if !event_names.insert(ev.name.to_ascii_lowercase()) {
                return Err(format!("Duplicate event definition '{}'", ev.name));
            }
            match ev.event_kind() {
                EventKind::Start => start_count += 1,
                EventKind::Refresh => refresh_count += 1,
                EventKind::Enter => enter_count += 1,
                EventKind::Load { grid: Some(ref g) } => match self.find_control(g) {
                    Some(c) if c.kind == ControlKind::Grid => {}
                    Some(_) => {
                        return Err(format!("Load event target '{g}' is not a Grid control"));
                    }
                    None => {
                        return Err(format!(
                            "Load event target Grid '{g}' does not exist in WebPanel layout"
                        ));
                    }
                },
                _ => {}
            }
            for (label, count) in [
                ("Start", start_count),
                ("Refresh", refresh_count),
                ("Enter", enter_count),
            ] {
                if count > 1 {
                    return Err(format!("Multiple {label} events defined in WebPanel"));
                }
            }
        }

        let mut seen_vars = HashSet::new();
        for v in &self.variables {
            v.validate()?;
            let norm_var = v.name.trim().trim_start_matches('&').to_ascii_lowercase();
            if !seen_vars.insert(norm_var) {
                return Err(format!("Duplicate ScreenVariable '{}'", v.name));
            }
        }

        Ok(())
    }

    fn validate_controls(
        &self,
        controls: &[LayoutControl],
        names: &mut HashSet<String>,
    ) -> Result<(), String> {
        for c in controls {
            if c.name.is_empty() {
                return Err("Control name cannot be empty".to_string());
            }
            let key = c.name.to_ascii_lowercase();
            if !names.insert(key) {
                return Err(format!("Duplicate control name '{}' in layout", c.name));
            }
            if c.kind == ControlKind::Button && c.event_name.is_none() {
                return Err(format!("Button '{}' must define an event_name", c.name));
            }
            if (c.kind == ControlKind::Attribute
                || c.kind == ControlKind::Variable
                || c.kind == ControlKind::Edit)
                && c.binding.is_none()
            {
                return Err(format!("Control '{}' requires a data binding", c.name));
            }
            self.validate_controls(&c.children, names)?;
        }
        Ok(())
    }

    /// Finds a control by name (case-insensitive).
    pub fn find_control<'a>(&'a self, name: &str) -> Option<&'a LayoutControl> {
        let needle = name.to_ascii_lowercase();
        fn recurse<'a>(controls: &'a [LayoutControl], needle: &str) -> Option<&'a LayoutControl> {
            for c in controls {
                if c.name.to_ascii_lowercase() == needle {
                    return Some(c);
                }
                if let Some(found) = recurse(&c.children, needle) {
                    return Some(found);
                }
            }
            None
        }
        recurse(&self.controls, &needle)
    }

    /// Finds an event by name (case-insensitive).
    pub fn find_event(&self, name: &str) -> Option<&WebPanelEvent> {
        let needle = name.to_ascii_lowercase();
        self.events
            .iter()
            .find(|e| e.name.to_ascii_lowercase() == needle)
    }

    /// Finds a screen variable by name (case-insensitive, ignoring leading '&').
    pub fn find_variable(&self, name: &str) -> Option<&ScreenVariable> {
        let needle = name.trim().trim_start_matches('&').to_ascii_lowercase();
        self.variables
            .iter()
            .find(|v| v.name.trim().trim_start_matches('&').to_ascii_lowercase() == needle)
    }

    /// Parses the authored string rules into structured Rule AST nodes.
    pub fn parse_rules(&self) -> Result<Vec<crate::rules::Rule>, String> {
        let mut normalized = String::new();
        for rule in &self.rules {
            let trimmed = rule.trim();
            if trimmed.is_empty() {
                continue;
            }
            normalized.push_str(trimmed);
            if !trimmed.ends_with(';') {
                normalized.push(';');
            }
            normalized.push('\n');
        }
        if normalized.trim().is_empty() {
            return Ok(Vec::new());
        }
        crate::rules::parse_rules(&normalized)
    }
}

fn parse_layout_controls(source: &str) -> Result<Vec<LayoutControl>, String> {
    let mut controls = Vec::new();
    for line in source.lines() {
        let l = line.trim();
        if l.is_empty() || l.starts_with("//") {
            continue;
        }

        let tokens: Vec<&str> = l.split_whitespace().collect();
        if tokens.len() < 2 {
            continue;
        }

        let kind = match tokens[0].to_ascii_lowercase().as_str() {
            "textblock" => ControlKind::TextBlock,
            "attribute" => ControlKind::Attribute,
            "variable" => ControlKind::Variable,
            "button" => ControlKind::Button,
            "grid" => ControlKind::Grid,
            "table" => ControlKind::Table,
            "responsivetable" => ControlKind::ResponsiveTable,
            "edit" => ControlKind::Edit,
            "canvas" => ControlKind::Canvas,
            "usercontrol" => ControlKind::UserControl,
            _ => continue,
        };

        let name = tokens[1].trim_matches('{').to_string();
        let mut control = LayoutControl {
            name,
            kind,
            caption: None,
            binding: None,
            event_name: None,
            theme_class: None,
            is_read_only: false,
            children: Vec::new(),
        };

        if let (Some(brace_start), Some(brace_end)) = (l.find('{'), l.rfind('}')) {
            let props_str = &l[brace_start + 1..brace_end];
            for prop in props_str.split(';') {
                let prop = prop.trim();
                if let Some((k, v)) = prop.split_once('=') {
                    let k = k.trim().to_ascii_lowercase();
                    let v = v.trim().trim_matches('"');
                    match k.as_str() {
                        "caption" => control.caption = Some(v.to_string()),
                        "binding" => control.binding = Some(v.to_string()),
                        "event" | "event_name" => control.event_name = Some(v.to_string()),
                        "class" | "theme_class" => control.theme_class = Some(v.to_string()),
                        "readonly" | "is_read_only" => {
                            control.is_read_only = v.eq_ignore_ascii_case("true")
                        }
                        _ => {}
                    }
                }
            }
        }

        // Default binding if attribute, variable, or edit name equals control name
        if control.binding.is_none()
            && (control.kind == ControlKind::Attribute
                || control.kind == ControlKind::Variable
                || control.kind == ControlKind::Edit)
        {
            control.binding = Some(control.name.clone());
        }

        controls.push(control);
    }
    Ok(controls)
}

fn parse_events(source: &str) -> Result<Vec<WebPanelEvent>, String> {
    let mut events = Vec::new();
    let mut current_name: Option<String> = None;
    let mut current_body = String::new();

    for line in source.lines() {
        let l = line.trim();
        if l.starts_with("Event ") {
            if let Some(name) = current_name.take() {
                events.push(WebPanelEvent::new(name, current_body.trim()));
                current_body.clear();
            }
            let name_part = l.strip_prefix("Event ").unwrap().trim();
            let event_name = name_part.trim_matches('\'').trim_matches('"').to_string();
            current_name = Some(event_name);
        } else if l.eq_ignore_ascii_case("EndEvent") {
            if let Some(name) = current_name.take() {
                events.push(WebPanelEvent::new(name, current_body.trim()));
                current_body.clear();
            }
        } else if current_name.is_some() {
            current_body.push_str(line);
            current_body.push('\n');
        }
    }

    if let Some(name) = current_name.take() {
        events.push(WebPanelEvent::new(name, current_body.trim()));
    }

    Ok(events)
}

fn parse_rules_block(source: &str) -> Result<Vec<String>, String> {
    let mut rules = Vec::new();
    for line in source.lines() {
        let l = line.trim();
        if l.is_empty() || l.starts_with("//") {
            continue;
        }
        for stmt in l.split(';') {
            let s = stmt.trim();
            if !s.is_empty() {
                rules.push(s.to_string());
            }
        }
    }
    Ok(rules)
}

fn parse_variables_block(source: &str) -> Result<Vec<ScreenVariable>, String> {
    let mut vars = Vec::new();
    for line in source.lines() {
        let l = line.trim().trim_matches(';');
        if l.is_empty() || l.starts_with("//") {
            continue;
        }
        if let Some((name_part, type_part)) = l.split_once(':') {
            let name = name_part.trim().to_string();
            let dt = parse_simple_data_type(type_part.trim())?;
            vars.push(ScreenVariable::new(name, dt));
        }
    }
    Ok(vars)
}

fn parse_simple_data_type(s: &str) -> Result<DataType, String> {
    let lower = s.to_ascii_lowercase();
    if lower.starts_with("numeric") {
        if let (Some(start), Some(end)) = (lower.find('('), lower.find(')')) {
            let inner = &lower[start + 1..end];
            if let Some((p_str, s_str)) = inner.split_once(',') {
                let precision: u8 = p_str.trim().parse().map_err(|e| format!("{e}"))?;
                let scale: u8 = s_str.trim().parse().map_err(|e| format!("{e}"))?;
                return Ok(DataType::Numeric { precision, scale });
            } else {
                let precision: u8 = inner.trim().parse().map_err(|e| format!("{e}"))?;
                return Ok(DataType::Numeric {
                    precision,
                    scale: 0,
                });
            }
        }
        return Ok(DataType::Numeric {
            precision: 10,
            scale: 0,
        });
    }
    if lower.starts_with("character") || lower.starts_with("char") {
        if let (Some(start), Some(end)) = (lower.find('('), lower.find(')')) {
            let inner = &lower[start + 1..end];
            let length: u32 = inner.trim().parse().map_err(|e| format!("{e}"))?;
            return Ok(DataType::Character { length });
        }
        return Ok(DataType::Character { length: 30 });
    }
    if lower.starts_with("varchar") {
        if let (Some(start), Some(end)) = (lower.find('('), lower.find(')')) {
            let inner = &lower[start + 1..end];
            let length: u32 = inner.trim().parse().map_err(|e| format!("{e}"))?;
            return Ok(DataType::Varchar { length });
        }
        return Ok(DataType::Varchar { length: 100 });
    }
    if lower == "boolean" || lower == "bool" {
        return Ok(DataType::Boolean {});
    }
    if lower == "date" {
        return Ok(DataType::Date {});
    }
    if lower == "datetime" {
        return Ok(DataType::DateTime {});
    }
    Err(format!("Unknown data type declaration '{s}'"))
}
