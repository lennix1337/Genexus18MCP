//! Builtin functions of the interpreter: each arm calls the same runtime operation the emitter
//! writes for that builtin (`typed_backend::lower_builtin_call`), on already evaluated arguments.

use crate::{
    date::Date,
    datetime::DateTime,
    record_runtime::Field,
    text::Text,
    types::{Decimal, format_str},
};

type R<T> = Result<T, String>;

struct Args {
    name: String,
    items: std::vec::IntoIter<Field>,
}

impl Args {
    fn new(name: &str, items: Vec<Field>) -> Self {
        Self {
            name: name.to_owned(),
            items: items.into_iter(),
        }
    }

    fn next(&mut self) -> R<Field> {
        self.items
            .next()
            .ok_or_else(|| format!("{} is missing an argument", self.name))
    }

    fn opt(&mut self) -> Option<Field> {
        self.items.next()
    }

    fn num(&mut self) -> R<Decimal> {
        match self.next()? {
            Field::Numeric(n) => Ok(n),
            _ => Err(format!(
                "{}: typed value mismatch, expected Numeric",
                self.name
            )),
        }
    }

    fn int(&mut self) -> R<i64> {
        self.num()?.to_i64()
    }

    fn text(&mut self) -> R<Text> {
        match self.next()? {
            Field::Text(t) => Ok(t),
            _ => Err(format!(
                "{}: typed value mismatch, expected Text",
                self.name
            )),
        }
    }

    fn boolean(&mut self) -> R<bool> {
        match self.next()? {
            Field::Boolean(b) => Ok(b),
            _ => Err(format!(
                "{}: typed value mismatch, expected Boolean",
                self.name
            )),
        }
    }
}

fn num_of(value: usize) -> R<Field> {
    Ok(Field::Numeric(value.to_string().parse::<Decimal>()?))
}

fn from_i64(value: i64) -> Field {
    Field::Numeric(Decimal::from(value))
}

fn txt(value: &str) -> R<Field> {
    Ok(Field::Text(Text::new(value)?))
}

fn date_or_time_text(value: &Field) -> R<Text> {
    match value {
        Field::Text(t) => Ok(t.clone()),
        Field::Numeric(n) => Text::new(&n.to_string()),
        Field::Boolean(b) => Ok(Text::bool_to_str(*b)),
        Field::Date(d) => Text::new(&d.to_text()),
        Field::DateTime(d) => Text::new(&d.to_text()),
        _ => Err("Format arguments must be scalar values".into()),
    }
}

pub(super) fn is_empty(value: Field) -> R<Field> {
    Ok(Field::Boolean(match value {
        Field::Date(d) => d.is_empty(),
        Field::DateTime(d) => d.is_empty(),
        Field::Collection(c) => c.is_empty(),
        Field::Numeric(n) => n.checked_cmp("0".parse::<Decimal>()?)?.is_eq(),
        Field::Text(t) => t.trim_end_spaces().utf16_length() == 0,
        Field::Boolean(b) => !b,
        Field::Record(_) => return Err("IsEmpty is not supported on records directly".into()),
    }))
}

/// `Year(d)`, `Month(d)`, ... on a Date or DateTime.
pub(super) fn date_part(method: &str, value: Field) -> R<Field> {
    let part = |date: [i64; 7]| -> R<Field> {
        let index = match method {
            "YEAR" => 0,
            "MONTH" => 1,
            "DAY" => 2,
            "HOUR" => 3,
            "MINUTE" => 4,
            "SECOND" | "SEC" => 5,
            "DOW" => 6,
            _ => return Err("unsupported calendar component".into()),
        };
        Ok(from_i64(date[index]))
    };
    match value {
        Field::Date(d) => part([
            d.year(),
            d.month(),
            d.day(),
            d.hour(),
            d.minute(),
            d.second(),
            d.day_of_week(),
        ]),
        Field::DateTime(d) => part([
            d.year(),
            d.month(),
            d.day(),
            d.hour(),
            d.minute(),
            d.second(),
            d.day_of_week(),
        ]),
        _ => Err("calendar method receiver must be Date or DateTime".into()),
    }
}

/// `AddMonths`/`AddYears`/`AddDays`/... and `Difference` as method calls.
pub(super) fn date_shift(method: &str, receiver: Field, offset: Field) -> R<Field> {
    if method == "DIFFERENCE" {
        return match (receiver, offset) {
            (Field::Date(a), Field::Date(b)) => Ok(from_i64(a.difference(b))),
            (Field::DateTime(a), Field::DateTime(b)) => Ok(from_i64(a.difference(b))),
            _ => Err("Difference requires two Date or two DateTime values".into()),
        };
    }
    let Field::Numeric(offset) = offset else {
        return Err("calendar shift offset must be Numeric".into());
    };
    let offset = offset.to_i64()?;
    match (receiver, method) {
        (Field::Date(d), "ADDMONTHS") => Ok(Field::Date(d.add_months(offset)?)),
        (Field::Date(d), "ADDYEARS") => Ok(Field::Date(d.add_years(offset)?)),
        (Field::Date(d), "ADDDAYS") => Ok(Field::Date(d.add_days(offset)?)),
        (Field::DateTime(d), "ADDMONTHS") => Ok(Field::DateTime(d.add_months(offset)?)),
        (Field::DateTime(d), "ADDYEARS") => Ok(Field::DateTime(d.add_years(offset)?)),
        (Field::DateTime(d), "ADDDAYS") => Ok(Field::DateTime(d.add_days(offset)?)),
        (Field::DateTime(d), "ADDHOURS") => Ok(Field::DateTime(d.add_hours(offset)?)),
        (Field::DateTime(d), "ADDMINUTES") => Ok(Field::DateTime(d.add_minutes(offset)?)),
        (Field::DateTime(d), "ADDSECONDS") => Ok(Field::DateTime(d.add_seconds(offset)?)),
        _ => Err("unsupported calendar shift receiver/method".into()),
    }
}

/// Builtins that evaluate all their arguments. `Iif`, `Nullvalue` and `Str` are in the caller.
pub(super) fn builtin_values(name: &str, values: Vec<Field>, window: Option<u16>) -> R<Field> {
    let mut a = Args::new(name, values);
    match name {
        "TRIM" => Ok(Field::Text(a.text()?.trim_spaces())),
        "LEN" => num_of(a.text()?.utf16_length()),
        "SUBSTR" => {
            let text = a.text()?;
            let start = a.int()?;
            let length = a.int()?;
            Ok(Field::Text(text.substring(start, length)))
        }
        "UPPER" => Ok(Field::Text(a.text()?.to_upper())),
        "LOWER" => Ok(Field::Text(a.text()?.to_lower())),
        "STRREPLACE" => {
            let text = a.text()?;
            let find = a.text()?;
            let with = a.text()?;
            Ok(Field::Text(text.str_replace(&find, &with)?))
        }
        "LTRIM" => Ok(Field::Text(a.text()?.trim_start_spaces())),
        "RTRIM" => Ok(Field::Text(a.text()?.trim_end_spaces())),
        "PADL" | "PADR" => {
            let text = a.text()?;
            let size = a.int()?;
            let fill = match a.opt() {
                Some(Field::Text(fill)) => fill,
                Some(_) => return Err("Pad fill must be Text".into()),
                None => Text::new(" ")?,
            };
            Ok(Field::Text(if name == "PADL" {
                text.pad_left(size, &fill)?
            } else {
                text.pad_right(size, &fill)?
            }))
        }
        "SPACE" => Ok(Field::Text(Text::space(a.int()?)?)),
        "CONCAT" => {
            let first = a.text()?;
            let second = a.text()?;
            let separator = match a.opt() {
                Some(Field::Text(separator)) => Some(separator),
                Some(_) => return Err("Concat separator must be Text".into()),
                None => None,
            };
            Ok(Field::Text(first.concat_with_sep(&second, separator.as_ref())?))
        }
        "STARTSWITH" | "ENDSWITH" | "CONTAINS" => {
            let first = a.text()?;
            let second = a.text()?;
            Ok(Field::Boolean(match name {
                "STARTSWITH" => first.starts_with(&second),
                "ENDSWITH" => first.ends_with(&second),
                _ => first.contains(&second),
            }))
        }
        "STRSEARCH" => {
            let first = a.text()?;
            let second = a.text()?;
            let start = match a.opt() {
                Some(Field::Numeric(start)) => start.to_i64()?,
                Some(_) => return Err("StrSearch start must be Numeric".into()),
                None => Decimal::from(1).to_i64()?,
            };
            Ok(Field::Numeric(
                first.str_search(&second, start).to_string().parse::<Decimal>()?,
            ))
        }
        "INDEXOF" => match a.next()? {
            Field::Collection(c) => match a.next()? {
                Field::Record(item) => Ok(from_i64(c.index_of(&item))),
                _ => Err("collection IndexOf item schema mismatch".into()),
            },
            Field::Text(first) => {
                let second = a.text()?;
                Ok(Field::Numeric(
                    first.str_search(&second, 1).to_string().parse::<Decimal>()?,
                ))
            }
            _ => Err("IndexOf receiver must be a text or collection".into()),
        },
        "INT" => Ok(from_i64(a.num()?.floor_to_i64()?)),
        "ROUND" => {
            let value = a.num()?;
            let digits = a.int()?;
            Ok(Field::Numeric(value.round(digits)?))
        }
        "TRUNC" => {
            let value = a.num()?;
            let digits = a.int()?;
            Ok(Field::Numeric(value.trunc(digits)?))
        }
        "VAL" => {
            let text = a.text()?;
            let separator = match a.opt() {
                Some(Field::Text(separator)) => Some(separator.to_string()),
                Some(_) => return Err("Val separator must be Text".into()),
                None => None,
            };
            Ok(Field::Numeric(Decimal::val(
                &text.to_string(),
                separator.as_deref(),
            )))
        }
        "LEFT" => {
            let text = a.text()?;
            Ok(Field::Text(text.left(a.int()?)))
        }
        "RIGHT" => {
            let text = a.text()?;
            Ok(Field::Text(text.right(a.int()?)?))
        }
        "ASC" => Ok(from_i64(a.text()?.asc())),
        "CHR" => Ok(Field::Text(Text::chr(a.int()?)?)),
        "NOTNUMERIC" => Ok(Field::Boolean(a.text()?.not_numeric())),
        "LTRIMSTR" => {
            let value = a.num()?;
            let length = a.int()?;
            let decimals = match a.opt() {
                Some(Field::Numeric(decimals)) => decimals.to_i64()?,
                Some(_) => return Err("LTrimStr decimals must be Numeric".into()),
                None => 0,
            };
            // `as u8` truncates exactly as the generated expression does.
            let formatted = format_str(value, length as u8, decimals as u8)?;
            txt(formatted.trim_start_matches(' '))
        }
        "DAY" | "MONTH" | "YEAR" | "HOUR" | "MINUTE" | "SECOND" | "SEC" | "DOW" => {
            date_part(name, a.next()?)
        }
        "ADDYR" | "ADDMTH" | "ADDDAYS" | "DADD" => {
            let receiver = a.next()?;
            let offset = a.int()?;
            match (receiver, name) {
                (Field::Date(d), "ADDYR") => Ok(Field::Date(d.add_years(offset)?)),
                (Field::Date(d), "ADDMTH") => Ok(Field::Date(d.add_months(offset)?)),
                (Field::Date(d), _) => Ok(Field::Date(d.add_days(offset)?)),
                (Field::DateTime(d), "ADDYR") => Ok(Field::DateTime(d.add_years(offset)?)),
                (Field::DateTime(d), "ADDMTH") => Ok(Field::DateTime(d.add_months(offset)?)),
                (Field::DateTime(d), _) => Ok(Field::DateTime(d.add_days(offset)?)),
                _ => Err(format!("{name} first argument must be Date or DateTime")),
            }
        }
        "TADD" => match a.next()? {
            Field::DateTime(d) => Ok(Field::DateTime(d.add_seconds(a.int()?)?)),
            _ => Err("TAdd expects a DateTime".into()),
        },
        "DDIFF" => match (a.next()?, a.next()?) {
            (Field::Date(x), Field::Date(y)) => Ok(from_i64(x.difference(y))),
            (Field::DateTime(x), Field::DateTime(y)) => Ok(from_i64(x.difference(y) / 86400)),
            _ => Err("DDiff requires two Date or two DateTime arguments".into()),
        },
        "TDIFF" => match (a.next()?, a.next()?) {
            (Field::DateTime(x), Field::DateTime(y)) => Ok(from_i64(x.difference(y))),
            _ => Err("TDiff expects two DateTime arguments".into()),
        },
        "AGE" => match (a.next()?, a.opt()) {
            (Field::Date(x), Some(Field::Date(y))) => Ok(from_i64(x.age(y))),
            (Field::DateTime(x), Some(Field::DateTime(y))) => Ok(from_i64(x.age(y))),
            (_, None) => Err("Age with 1 argument requires system clock which is not supported in deterministic engine".into()),
            _ => Err("Age arguments must both be Date or both DateTime".into()),
        },
        "ENDOFMONTH" => match a.next()? {
            Field::Date(d) => Ok(Field::Date(d.end_of_month())),
            Field::DateTime(d) => Ok(Field::DateTime(d.end_of_month())),
            _ => Err("EndOfMonth argument must be Date or DateTime".into()),
        },
        "RESETTIME" => match a.next()? {
            Field::Date(d) => Ok(Field::Date(d.reset_time())),
            Field::DateTime(d) => Ok(Field::DateTime(d.reset_time())),
            _ => Err("ResetTime argument must be Date or DateTime".into()),
        },
        "TOSTRING" => match a.next()? {
            Field::Boolean(b) => txt(if b { "true" } else { "false" }),
            Field::Record(_) | Field::Collection(_) => {
                Err("ToString receiver must be a primitive value".into())
            }
            other => Ok(Field::Text(date_or_time_text(&other)?)),
        },
        "YMDHMSTOT" => {
            let mut parts = [0_i64; 6];
            for part in &mut parts {
                *part = a.int()?;
            }
            Ok(Field::DateTime(DateTime::from_parts(parts, window)?))
        }
        "YMDTOD" => {
            let year = a.int()?;
            let month = a.int()?;
            let day = a.int()?;
            Ok(Field::Date(Date::ymd_with_window(year, month, day, window)?))
        }
        "CTOD" => {
            let text = a.text()?;
            Ok(Field::Date(Date::parse_cto_d(text.trim_str(), window)))
        }
        "DTOC" => match a.next()? {
            Field::Date(d) => txt(&d.to_iso_text()),
            _ => Err("DToC argument must be Date".into()),
        },
        "TTOC" => match a.next()? {
            Field::DateTime(d) => txt(&d.to_iso_text()),
            _ => Err("TToC argument must be DateTime".into()),
        },
        "ISEMPTY" => is_empty(a.next()?),
        "FORMAT" => {
            let pattern = a.text()?;
            let mut rest = Vec::new();
            while let Some(value) = a.opt() {
                rest.push(date_or_time_text(&value)?);
            }
            Ok(Field::Text(Text::format_template(&pattern, &rest)?))
        }
        "NEWLINE" => Ok(Field::Text(Text::newline())),
        "REPLICATE" => {
            let text = a.text()?;
            Ok(Field::Text(text.replicate(a.int()?)?))
        }
        "CHARAT" => {
            let text = a.text()?;
            Ok(Field::Text(text.char_at(a.int()?)))
        }
        "STRSEARCHREV" => {
            let first = a.text()?;
            let second = a.text()?;
            let start = match a.opt() {
                Some(Field::Numeric(start)) => start.to_i64()?,
                Some(_) => return Err("StrSearchRev start must be Numeric".into()),
                None => -1,
            };
            Ok(from_i64(first.str_search_rev(&second, start)))
        }
        "BOOLTOSTR" => Ok(Field::Text(Text::bool_to_str(a.boolean()?))),
        "TOBOOLEAN" | "STRTOBOOL" => Ok(Field::Boolean(a.text()?.to_boolean())),
        "TOJSON" | "TOJSONSTRING" => match a.next()? {
            Field::Record(r) => txt(&r.to_json()),
            Field::Collection(c) => txt(&c.to_json()),
            _ => Err(format!("{name} argument must be a record or collection")),
        },
        "FROMJSON" | "FROMJSONSTRING" => {
            let target = a.next()?;
            let json = a.text()?;
            match target {
                Field::Record(r) => Ok(Field::Boolean(r.from_json(json.as_str()))),
                Field::Collection(c) => Ok(Field::Boolean(c.from_json(json.as_str()))),
                _ => Err(format!("{name} target must be a record or collection")),
            }
        }
        "TOXML" => {
            let target = a.next()?;
            let root = match a.opt() {
                Some(Field::Text(root)) => Some(root),
                Some(_) => return Err("ToXml root must be Text".into()),
                None => None,
            };
            let root = root.as_ref().map(Text::as_str);
            match target {
                Field::Record(r) => txt(&r.to_xml(root)),
                Field::Collection(c) => txt(&c.to_xml(root)),
                _ => Err("ToXml argument must be a record or collection".into()),
            }
        }
        "FROMXML" => {
            let target = a.next()?;
            let xml = a.text()?;
            let root = match a.opt() {
                Some(Field::Text(root)) => Some(root),
                Some(_) => return Err("FromXml root must be Text".into()),
                None => None,
            };
            let root = root.as_ref().map(Text::as_str);
            match target {
                Field::Record(r) => Ok(Field::Boolean(r.from_xml(xml.as_str(), root))),
                Field::Collection(c) => Ok(Field::Boolean(c.from_xml(xml.as_str(), root))),
                _ => Err("FromXml target must be a record or collection".into()),
            }
        }
        other => Err(format!("builtin {other} is not implemented by the interpreter")),
    }
}
