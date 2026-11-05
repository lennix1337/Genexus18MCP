//! Stored datetime and marker parsing for UDM model reads.

use super::{ROW_ABSENT, ROW_PRESENT};

pub(super) fn parse_row_marker(text: &str, row: &str) -> Result<bool, String> {
    match text.trim() {
        ROW_PRESENT => Ok(true),
        ROW_ABSENT => Ok(false),
        other => Err(format!(
            "{row} presence marker is '{other}'; expected {ROW_PRESENT} or {ROW_ABSENT}"
        )),
    }
}

/// A stored `NULL` arrives as an empty `ISNULL(..., '')` field; a real value never is.
pub(super) fn non_empty(text: &str) -> Option<String> {
    let text = text.trim();
    (!text.is_empty()).then(|| text.to_string())
}

/// Parses a `CONVERT(VARCHAR(33), <datetime>, 126)` value (`YYYY-MM-DDTHH:MM:SS[.fffffff]`)
/// into raw .NET ticks, with no time-zone conversion.
///
/// `datetime` columns display three fraction digits and `datetime2` up to seven; padding
/// to seven preserves the displayed value. The token was matched on stored values whose
/// fractions were already zero, so a `datetime` column carrying a non-zero 1/300-second
/// fraction remains uncertified rather than silently rounded here.
pub(super) fn parse_stored_datetime_ticks(text: &str, column: &str) -> Result<Option<i64>, String> {
    let text = text.trim();
    if text.is_empty() {
        return Ok(None);
    }
    parse_datetime_ticks(text)
        .map(Some)
        .map_err(|reason| format!("{column} '{text}' is not a valid datetime: {reason}"))
}

pub(crate) fn parse_datetime_ticks(text: &str) -> Result<i64, String> {
    let (date, time) = text
        .split_once('T')
        .ok_or_else(|| "missing 'T' separator".to_string())?;
    let mut date_parts = date.split('-');
    let year = fixed_digits(date_parts.next(), 4, "year")?;
    let month = fixed_digits(date_parts.next(), 2, "month")?;
    let day = fixed_digits(date_parts.next(), 2, "day")?;
    if date_parts.next().is_some() {
        return Err("date has more than three parts".to_string());
    }

    let (clock, fraction) = match time.split_once('.') {
        Some((clock, fraction)) => (clock, Some(fraction)),
        None => (time, None),
    };
    let mut time_parts = clock.split(':');
    let hour = fixed_digits(time_parts.next(), 2, "hour")?;
    let minute = fixed_digits(time_parts.next(), 2, "minute")?;
    let second = fixed_digits(time_parts.next(), 2, "second")?;
    if time_parts.next().is_some() {
        return Err("time has more than three parts".to_string());
    }

    let fraction_ticks = match fraction {
        None => 0,
        Some(fraction) => {
            if fraction.is_empty() || fraction.len() > 7 {
                return Err("fraction must have 1 to 7 digits".to_string());
            }
            if !fraction.bytes().all(|byte| byte.is_ascii_digit()) {
                return Err("fraction contains a non-digit".to_string());
            }
            let mut padded = fraction.to_string();
            while padded.len() < 7 {
                padded.push('0');
            }
            padded
                .parse::<i64>()
                .map_err(|_| "fraction is not a number".to_string())?
        }
    };

    if !(1..=9999).contains(&year) {
        return Err("year is outside 1..=9999".to_string());
    }
    if !(1..=12).contains(&month) {
        return Err("month is outside 1..=12".to_string());
    }
    if day < 1 || day > days_in_month(year, month) {
        return Err("day is outside the month".to_string());
    }
    if !(0..=23).contains(&hour) {
        return Err("hour is outside 0..=23".to_string());
    }
    if !(0..=59).contains(&minute) {
        return Err("minute is outside 0..=59".to_string());
    }
    if !(0..=59).contains(&second) {
        return Err("second is outside 0..=59".to_string());
    }

    let days = days_from_civil(year, month, day);
    let seconds = hour * 3600 + minute * 60 + second;
    let ticks =
        621_355_968_000_000_000 + days * 864_000_000_000 + seconds * 10_000_000 + fraction_ticks;
    crate::read_token::UtcTicks::new(ticks)
        .map(crate::read_token::UtcTicks::get)
        .map_err(|error| error.to_string())
}

fn fixed_digits(text: Option<&str>, len: usize, field: &str) -> Result<i64, String> {
    let text = text.ok_or_else(|| format!("missing {field}"))?;
    if text.len() != len || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(format!("{field} is not {len} digits"));
    }
    text.parse::<i64>()
        .map_err(|_| format!("{field} is not a number"))
}

fn days_in_month(year: i64, month: i64) -> i64 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) => 29,
        2 => 28,
        _ => 0,
    }
}

/// Days since 1970-01-01 (Howard Hinnant's `days_from_civil`), valid for the proleptic
/// Gregorian calendar the .NET tick epoch uses.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400;
    let month_prime = (month + 9) % 12;
    let day_of_year = (153 * month_prime + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}
