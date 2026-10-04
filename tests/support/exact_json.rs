//! A JSON value for the fixture that keeps number tokens exact.
//!
//! The crate deliberately does not enable serde_json's `arbitrary_precision`
//! (feature unification would also change the library under test), so
//! `serde_json::Value` would round large or long numbers through `f64`.
//! [`J`] parses through `RawValue` instead and keeps every number as its
//! original token; [`Dec`] gives exact comparison and addition.

#![allow(dead_code)]

use std::cmp::Ordering;
use std::collections::BTreeMap;
use std::fmt;

use serde_json::value::RawValue;

pub type Obj = BTreeMap<String, J>;

#[derive(Clone, Debug, PartialEq)]
pub enum J {
    Null,
    Bool(bool),
    /// The exact number token.
    Num(String),
    Str(String),
    Arr(Vec<J>),
    Obj(Obj),
}

static NULL: J = J::Null;

impl J {
    /// Parses one JSON text, keeping number tokens exact.
    pub fn parse(text: &str) -> Option<J> {
        let raw: &RawValue = serde_json::from_str(text).ok()?;
        Self::from_raw(raw.get())
    }

    pub fn parse_bytes(bytes: &[u8]) -> Option<J> {
        Self::parse(std::str::from_utf8(bytes).ok()?)
    }

    fn from_raw(text: &str) -> Option<J> {
        let t = text.trim();
        Some(match t.as_bytes().first()? {
            b'{' => {
                let m: BTreeMap<String, &RawValue> = serde_json::from_str(t).ok()?;
                J::Obj(
                    m.into_iter()
                        .map(|(k, v)| Some((k, Self::from_raw(v.get())?)))
                        .collect::<Option<_>>()?,
                )
            }
            b'[' => {
                let a: Vec<&RawValue> = serde_json::from_str(t).ok()?;
                J::Arr(
                    a.into_iter()
                        .map(|v| Self::from_raw(v.get()))
                        .collect::<Option<_>>()?,
                )
            }
            b'"' => J::Str(serde_json::from_str(t).ok()?),
            b't' => J::Bool(true),
            b'f' => J::Bool(false),
            b'n' => J::Null,
            _ => {
                // Out-of-range numbers (more than 128 digits or a scale
                // beyond ±1024) are rejected, so no later step expands them.
                if !Dec::parse(t)?.in_range() {
                    return None;
                }
                J::Num(t.to_owned())
            }
        })
    }

    pub fn as_object(&self) -> Option<&Obj> {
        match self {
            J::Obj(m) => Some(m),
            _ => None,
        }
    }

    pub fn as_object_mut(&mut self) -> Option<&mut Obj> {
        match self {
            J::Obj(m) => Some(m),
            _ => None,
        }
    }

    pub fn as_array(&self) -> Option<&Vec<J>> {
        match self {
            J::Arr(a) => Some(a),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            J::Str(s) => Some(s),
            _ => None,
        }
    }

    /// A plain non-negative integer token in `u64` range.
    pub fn as_u64(&self) -> Option<u64> {
        match self {
            J::Num(n) if n.bytes().all(|b| b.is_ascii_digit()) => n.parse().ok(),
            _ => None,
        }
    }

    pub fn is_null(&self) -> bool {
        matches!(self, J::Null)
    }

    pub fn is_string(&self) -> bool {
        matches!(self, J::Str(_))
    }

    pub fn obj() -> J {
        J::Obj(Obj::new())
    }

    pub fn to_json(&self) -> String {
        let mut out = String::new();
        self.write(&mut out);
        out
    }

    fn write(&self, out: &mut String) {
        match self {
            J::Null => out.push_str("null"),
            J::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
            J::Num(n) => out.push_str(n),
            J::Str(s) => out.push_str(&serde_json::to_string(s).unwrap()),
            J::Arr(a) => {
                out.push('[');
                for (i, v) in a.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    v.write(out);
                }
                out.push(']');
            }
            J::Obj(m) => {
                out.push('{');
                for (i, (k, v)) in m.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    out.push_str(&serde_json::to_string(k).unwrap());
                    out.push(':');
                    v.write(out);
                }
                out.push('}');
            }
        }
    }
}

impl fmt::Display for J {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.to_json())
    }
}

impl std::ops::Index<&str> for J {
    type Output = J;
    fn index(&self, key: &str) -> &J {
        self.as_object().and_then(|m| m.get(key)).unwrap_or(&NULL)
    }
}

/// Coefficient digit bound and exponent range (named-json-document-contract:
/// at most 128 significant digits, normalized scales within ±1024).
pub const MAX_DIGITS: usize = 128;
pub const MAX_SCALE: i64 = 1024;

/// An exact decimal: `(-1)^neg × digits × 10^exp`, with `digits` free of
/// leading and trailing zeros (empty for zero).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Dec {
    neg: bool,
    digits: Vec<u8>,
    exp: i64,
}

impl Dec {
    pub fn parse(text: &str) -> Option<Dec> {
        let b = text.as_bytes();
        let mut i = 0;
        let neg = b.first() == Some(&b'-');
        if neg {
            i += 1;
        }
        let int_start = i;
        while i < b.len() && b[i].is_ascii_digit() {
            i += 1;
        }
        let int = &b[int_start..i];
        if int.is_empty() || (int.len() > 1 && int[0] == b'0') {
            return None;
        }
        let mut frac: &[u8] = &[];
        if b.get(i) == Some(&b'.') {
            let s = i + 1;
            i = s;
            while i < b.len() && b[i].is_ascii_digit() {
                i += 1;
            }
            frac = &b[s..i];
            if frac.is_empty() {
                return None;
            }
        }
        let mut exp: i64 = 0;
        if matches!(b.get(i), Some(b'e' | b'E')) {
            i += 1;
            let eneg = b.get(i) == Some(&b'-');
            if matches!(b.get(i), Some(b'+' | b'-')) {
                i += 1;
            }
            let s = i;
            while i < b.len() && b[i].is_ascii_digit() {
                i += 1;
            }
            if s == i {
                return None;
            }
            // Leading exponent zeros are valid JSON; bound the magnitude only.
            let sig = std::str::from_utf8(&b[s..i]).ok()?.trim_start_matches('0');
            if sig.len() > 9 {
                return None;
            }
            exp = if sig.is_empty() { 0 } else { sig.parse().ok()? };
            if eneg {
                exp = -exp;
            }
        }
        if i != b.len() {
            return None;
        }
        let digits: Vec<u8> = int.iter().chain(frac).map(|d| d - b'0').collect();
        Some(Self::normalize(neg, digits, exp - frac.len() as i64))
    }

    fn normalize(neg: bool, mut digits: Vec<u8>, mut exp: i64) -> Dec {
        let lead = digits.iter().take_while(|d| **d == 0).count();
        digits.drain(..lead);
        while digits.last() == Some(&0) {
            digits.pop();
            exp += 1;
        }
        if digits.is_empty() {
            return Dec {
                neg: false,
                digits,
                exp: 0,
            };
        }
        Dec { neg, digits, exp }
    }

    pub fn is_zero(&self) -> bool {
        self.digits.is_empty()
    }

    /// Within the contract's digit and scale bounds.
    pub fn in_range(&self) -> bool {
        self.digits.len() <= MAX_DIGITS && self.exp.abs() <= MAX_SCALE
    }

    fn cmp_magnitude(&self, other: &Dec) -> Ordering {
        // Position of the most significant digit, then the digits.
        let (ma, mb) = (
            self.digits.len() as i64 + self.exp,
            other.digits.len() as i64 + other.exp,
        );
        ma.cmp(&mb).then_with(|| self.digits.cmp(&other.digits))
    }

    /// Exact addition; `None` when an operand or the result is out of range.
    pub fn add(&self, other: &Dec) -> Option<Dec> {
        if !self.in_range() || !other.in_range() {
            return None;
        }
        let exp = self.exp.min(other.exp);
        let widen = |d: &Dec| -> Vec<u8> {
            let mut v = d.digits.clone();
            v.extend(std::iter::repeat_n(0, (d.exp - exp) as usize));
            v
        };
        let (a, b) = (widen(self), widen(other));
        let (neg, digits) = if self.neg == other.neg {
            (self.neg, add_mag(&a, &b))
        } else {
            match cmp_mag(&a, &b) {
                Ordering::Equal => (false, Vec::new()),
                Ordering::Greater => (self.neg, sub_mag(&a, &b)),
                Ordering::Less => (other.neg, sub_mag(&b, &a)),
            }
        };
        let out = Self::normalize(neg, digits, exp);
        out.in_range().then_some(out)
    }

    /// Plain decimal text (no exponent).
    pub fn to_plain(&self) -> String {
        if self.is_zero() {
            return "0".into();
        }
        let ds: String = self.digits.iter().map(|d| char::from(b'0' + d)).collect();
        let body = if self.exp >= 0 {
            format!("{ds}{}", "0".repeat(self.exp as usize))
        } else {
            let point = ds.len() as i64 + self.exp;
            if point > 0 {
                format!("{}.{}", &ds[..point as usize], &ds[point as usize..])
            } else {
                format!("0.{}{ds}", "0".repeat((-point) as usize))
            }
        };
        if self.neg {
            format!("-{body}")
        } else {
            body
        }
    }
}

fn strip(v: &[u8]) -> &[u8] {
    let lead = v.iter().take_while(|d| **d == 0).count();
    &v[lead..]
}

fn cmp_mag(a: &[u8], b: &[u8]) -> Ordering {
    let (a, b) = (strip(a), strip(b));
    a.len().cmp(&b.len()).then_with(|| a.cmp(b))
}

fn add_mag(a: &[u8], b: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(a.len().max(b.len()) + 1);
    let (mut i, mut j, mut carry) = (a.len(), b.len(), 0u8);
    while i > 0 || j > 0 || carry > 0 {
        let mut s = carry;
        if i > 0 {
            i -= 1;
            s += a[i];
        }
        if j > 0 {
            j -= 1;
            s += b[j];
        }
        out.push(s % 10);
        carry = s / 10;
    }
    out.reverse();
    out
}

/// `a - b` for `a >= b`.
fn sub_mag(a: &[u8], b: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(a.len());
    let (mut i, mut j, mut borrow) = (a.len(), b.len(), 0i8);
    while i > 0 {
        i -= 1;
        let mut d = a[i] as i8 - borrow;
        if j > 0 {
            j -= 1;
            d -= b[j] as i8;
        }
        borrow = if d < 0 { 1 } else { 0 };
        out.push((d + 10 * borrow) as u8);
    }
    out.reverse();
    out
}

impl PartialOrd for Dec {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for Dec {
    fn cmp(&self, other: &Self) -> Ordering {
        match (self.is_zero(), other.is_zero()) {
            (true, true) => return Ordering::Equal,
            (true, false) => {
                return if other.neg {
                    Ordering::Greater
                } else {
                    Ordering::Less
                }
            }
            (false, true) => {
                return if self.neg {
                    Ordering::Less
                } else {
                    Ordering::Greater
                }
            }
            _ => {}
        }
        match (self.neg, other.neg) {
            (false, true) => Ordering::Greater,
            (true, false) => Ordering::Less,
            (false, false) => self.cmp_magnitude(other),
            (true, true) => other.cmp_magnitude(self),
        }
    }
}

/// Unicode default case folding (the `lower` transform), as tb-server does
/// with the `caseless` crate.
pub fn fold(s: &str) -> String {
    caseless::default_case_fold_str(s)
}
