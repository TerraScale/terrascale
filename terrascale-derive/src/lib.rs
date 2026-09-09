//! `#[derive(Schema)]` for the TerraScale Rust SDK.
//!
//! The derive implements `terrascale::schema::Document` for named-field and
//! unit structs, emitting the collection name and field metadata. `Option<T>`
//! fields are optional. Scalar types with an unambiguous wire representation
//! are inferred; other application types fall back to JSON and can override
//! that with `#[schema(type = "...")]`.
//!
//! Container attributes include `collection`/`name`, `ttl`/`ttl_field`,
//! `history`/`history_window_days`/`history_retention_days`, and
//! `merge_policy`. Field attributes include `rename`, `type`, `indexed`,
//! `unique`, `optional`, `merge`, and `skip`. Serde `rename`, `rename_all`, and
//! `skip` are also honored for schema metadata.

use proc_macro::TokenStream;
use quote::quote;
use syn::{parse_macro_input, Data, DeriveInput, Fields, Meta, Type};

#[derive(Default)]
struct Attributes {
    rename: Option<String>,
    field_type: Option<String>,
    indexed: bool,
    unique: bool,
    optional: bool,
    merge: Option<String>,
    skip: bool,
}

#[derive(Default)]
struct CollectionAttributes {
    name: Option<String>,
    ttl: Option<String>,
    history_window_days: Option<u8>,
    merge_policy: Option<String>,
    rename_all: Option<String>,
}

fn parse_attrs(attrs: &[syn::Attribute], field: bool) -> syn::Result<Attributes> {
    let mut output = Attributes::default();
    for attr in attrs {
        if attr.path().is_ident("serde") {
            let Meta::List(list) = &attr.meta else {
                continue;
            };
            list.parse_nested_meta(|meta| {
                if meta.path.is_ident("skip") {
                    output.skip = true;
                } else if meta.path.is_ident("rename") {
                    if let Ok(value) = meta.value() {
                        let lit: syn::LitStr = value.parse()?;
                        output.rename = Some(lit.value());
                    }
                }
                Ok(())
            })?;
        } else if field && attr.path().is_ident("schema") {
            let Meta::List(list) = &attr.meta else {
                continue;
            };
            list.parse_nested_meta(|meta| {
                if meta.path.is_ident("indexed") {
                    output.indexed = true;
                } else if meta.path.is_ident("unique") {
                    output.unique = true;
                } else if meta.path.is_ident("optional") {
                    output.optional = true;
                } else if meta.path.is_ident("skip") {
                    output.skip = true;
                } else if meta.path.is_ident("rename")
                    || meta.path.is_ident("type")
                    || meta.path.is_ident("merge")
                {
                    let value = meta.value()?;
                    let lit: syn::LitStr = value.parse()?;
                    if meta.path.is_ident("rename") {
                        output.rename = Some(lit.value());
                    } else if meta.path.is_ident("type") {
                        output.field_type = Some(lit.value());
                    } else {
                        output.merge = Some(lit.value());
                    }
                } else {
                    return Err(meta.error("unsupported schema field attribute"));
                }
                Ok(())
            })?;
        }
    }
    Ok(output)
}

fn collection_attributes(
    attrs: &[syn::Attribute],
    fallback: String,
) -> syn::Result<CollectionAttributes> {
    let mut output = CollectionAttributes {
        name: Some(fallback),
        ..Default::default()
    };
    for attr in attrs {
        if attr.path().is_ident("serde") {
            let Meta::List(list) = &attr.meta else {
                continue;
            };
            list.parse_nested_meta(|meta| {
                if meta.path.is_ident("rename_all") {
                    let value = meta.value()?;
                    let lit: syn::LitStr = value.parse()?;
                    output.rename_all = Some(lit.value());
                }
                Ok(())
            })?;
        } else if attr.path().is_ident("schema") {
            let Meta::List(list) = &attr.meta else {
                continue;
            };
            list.parse_nested_meta(|meta| {
                if meta.path.is_ident("collection") || meta.path.is_ident("name") {
                    let value = meta.value()?;
                    let lit: syn::LitStr = value.parse()?;
                    output.name = Some(lit.value());
                } else if meta.path.is_ident("ttl") || meta.path.is_ident("ttl_field") {
                    let value = meta.value()?;
                    let lit: syn::LitStr = value.parse()?;
                    output.ttl = Some(lit.value());
                } else if meta.path.is_ident("history")
                    || meta.path.is_ident("history_window_days")
                    || meta.path.is_ident("history_retention_days")
                {
                    let value = meta.value()?;
                    let lit: syn::LitInt = value.parse()?;
                    output.history_window_days = Some(lit.base10_parse()?);
                } else if meta.path.is_ident("merge_policy") {
                    let value = meta.value()?;
                    let lit: syn::LitStr = value.parse()?;
                    output.merge_policy = Some(lit.value());
                } else {
                    return Err(meta.error("unsupported schema container attribute"));
                }
                Ok(())
            })?;
        }
    }
    Ok(output)
}

fn split_words(name: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = name.chars().collect();
    for (index, character) in chars.iter().copied().enumerate() {
        let previous = index
            .checked_sub(1)
            .and_then(|position| chars.get(position));
        let next = chars.get(index + 1);
        let boundary = character == '_'
            || character == '-'
            || character.is_whitespace()
            || (character.is_ascii_uppercase()
                && previous.is_some_and(|value| value.is_ascii_lowercase())
                || character.is_ascii_uppercase()
                    && previous.is_some_and(|value| value.is_ascii_uppercase())
                    && next.is_some_and(|value| value.is_ascii_lowercase()));
        if boundary {
            if !current.is_empty() {
                words.push(std::mem::take(&mut current));
            }
            if character != '_' && character != '-' && !character.is_whitespace() {
                current.push(character.to_ascii_lowercase());
            }
        } else {
            current.push(character.to_ascii_lowercase());
        }
    }
    if !current.is_empty() {
        words.push(current);
    }
    words
}

fn rename_field(name: &str, rule: Option<&str>) -> syn::Result<String> {
    let Some(rule) = rule else {
        return Ok(name.to_owned());
    };
    let words = split_words(name);
    let renamed = match rule {
        "camelCase" => words
            .first()
            .cloned()
            .into_iter()
            .chain(words.iter().skip(1).map(|word| {
                let mut chars = word.chars();
                chars
                    .next()
                    .map_or_else(String::new, |first| first.to_ascii_uppercase().to_string())
                    + chars.as_str()
            }))
            .collect(),
        "snake_case" => words.join("_"),
        "kebab-case" => words.join("-"),
        "SCREAMING_SNAKE_CASE" => words.join("_").to_ascii_uppercase(),
        "SCREAMING-KEBAB-CASE" => words.join("-").to_ascii_uppercase(),
        "PascalCase" => words
            .iter()
            .map(|word| {
                let mut chars = word.chars();
                chars
                    .next()
                    .map_or_else(String::new, |first| first.to_ascii_uppercase().to_string())
                    + chars.as_str()
            })
            .collect(),
        "lowercase" => words.concat(),
        "UPPERCASE" => words.concat().to_ascii_uppercase(),
        _ => {
            return Err(syn::Error::new(
                proc_macro2::Span::call_site(),
                format!("unsupported serde rename_all rule {rule:?}"),
            ));
        }
    };
    Ok(renamed)
}

fn field_type_name(ty: &Type) -> (&'static str, bool) {
    let Type::Path(path) = ty else {
        return ("json", false);
    };
    let Some(segment) = path.path.segments.last() else {
        return ("json", false);
    };
    let name = segment.ident.to_string();
    if name == "Option" {
        if let syn::PathArguments::AngleBracketed(args) = &segment.arguments {
            if let Some(syn::GenericArgument::Type(inner)) = args.args.first() {
                let (kind, _) = field_type_name(inner);
                return (kind, true);
            }
        }
        return ("json", true);
    }
    match name.as_str() {
        "String" | "str" => ("string", false),
        "bool" => ("bool", false),
        "i8" | "i16" | "i32" | "i64" | "i128" | "isize" | "u8" | "u16" | "u32" | "u64" | "u128"
        | "usize" => ("int", false),
        "f32" | "f64" => ("float", false),
        "Vec" => {
            if let syn::PathArguments::AngleBracketed(args) = &segment.arguments {
                if let Some(syn::GenericArgument::Type(Type::Path(inner))) = args.args.first() {
                    if inner
                        .path
                        .segments
                        .last()
                        .is_some_and(|part| part.ident == "u8")
                    {
                        return ("bytes", false);
                    }
                }
            }
            ("json", false)
        }
        "Value" | "JsonValue" => ("json", false),
        "SystemTime" | "Instant" | "Timestamp" | "DateTime" => ("timestamp", false),
        _ => ("json", false),
    }
}

fn field_type_token(value: &str) -> syn::Result<proc_macro2::TokenStream> {
    let variant = match value {
        "string" => quote!(String),
        "int" | "integer" => quote!(Integer),
        "float" => quote!(Float),
        "bool" | "boolean" => quote!(Boolean),
        "json" => quote!(Json),
        "bytes" => quote!(Bytes),
        "timestamp" => quote!(Timestamp),
        "text" => quote!(Text),
        "counter-pn" | "pn-counter" => quote!(PnCounter),
        "set-add-wins" | "add-wins-set" => quote!(AddWinsSet),
        "set-remove-wins" | "remove-wins-set" => quote!(RemoveWinsSet),
        "register-mv" | "mv-register" => quote!(MvRegister),
        _ => {
            return Err(syn::Error::new(
                proc_macro2::Span::call_site(),
                format!("unknown schema field type {value:?}"),
            ))
        }
    };
    Ok(quote!(::terrascale::schema::FieldType::#variant))
}

fn merge_policy_token(value: &str) -> syn::Result<proc_macro2::TokenStream> {
    let variant = match value {
        "remove-wins" | "remove_wins" => quote!(RemoveWins),
        "hlc-tiebreak" | "hlc_tiebreak" => quote!(HlcTiebreak),
        "last-write-wins" | "last_write_wins" => quote!(LastWriteWins),
        _ => {
            return Err(syn::Error::new(
                proc_macro2::Span::call_site(),
                format!("unknown schema merge policy {value:?}"),
            ))
        }
    };
    Ok(quote!(::terrascale::schema::MergePolicy::#variant))
}

fn merge_type_token(value: &str) -> syn::Result<proc_macro2::TokenStream> {
    field_type_token(value)
}

/// Derive `terrascale::schema::Document` from a named-field struct.
#[proc_macro_derive(Schema, attributes(schema))]
pub fn derive_schema(input: TokenStream) -> TokenStream {
    let ast = parse_macro_input!(input as DeriveInput);
    match expand_schema(ast) {
        Ok(tokens) => tokens.into(),
        Err(error) => error.to_compile_error().into(),
    }
}

fn expand_schema(ast: DeriveInput) -> syn::Result<proc_macro2::TokenStream> {
    let name = ast.ident;
    let generics = ast.generics;
    let fallback = name.to_string().to_lowercase();
    let collection = collection_attributes(&ast.attrs, fallback)?;
    let rename_all = collection.rename_all.as_deref();
    let fields = match ast.data {
        Data::Struct(data) => match data.fields {
            Fields::Named(fields) => fields.named,
            Fields::Unit => syn::punctuated::Punctuated::new(),
            Fields::Unnamed(_) => {
                return Err(syn::Error::new_spanned(
                    name,
                    "Schema can only derive for named-field structs",
                ));
            }
        },
        _ => {
            return Err(syn::Error::new_spanned(
                name,
                "Schema can only derive for structs",
            ))
        }
    };

    let mut field_tokens = Vec::new();
    for field in fields {
        let Some(ident) = field.ident else { continue };
        let attrs = parse_attrs(&field.attrs, true)?;
        if attrs.skip {
            continue;
        }
        let (inferred, option) = field_type_name(&field.ty);
        let field_type = field_type_token(attrs.field_type.as_deref().unwrap_or(inferred))?;
        let optional = option || attrs.optional;
        let builder = if optional {
            quote!(::terrascale::schema::FieldDef::optional)
        } else {
            quote!(::terrascale::schema::FieldDef::required)
        };
        let field_name = match attrs.rename {
            Some(rename) => rename,
            None => rename_field(&ident.to_string(), rename_all)?,
        };
        let mut declaration = quote!(#builder(#field_name, #field_type));
        if attrs.indexed {
            declaration = quote!(#declaration.indexed());
        }
        if attrs.unique {
            declaration = quote!(#declaration.unique());
        }
        if let Some(merge) = attrs.merge {
            let merge_type = merge_type_token(&merge)?;
            declaration = quote!(#declaration.merge_policy(#merge_type));
        }
        field_tokens.push(quote! { schema = schema.field(#declaration); });
    }

    let collection_name = collection.name.expect("collection fallback is always set");
    let history = collection.history_window_days.map(|days| {
        quote! {
            schema = schema.history_window_days(#days);
        }
    });
    let ttl = collection.ttl.map(|field| {
        quote! {
            schema = schema.ttl(#field);
        }
    });
    let merge_policy = collection
        .merge_policy
        .map(|policy| merge_policy_token(&policy))
        .transpose()?;
    let merge_policy = merge_policy.map(|policy| {
        quote! {
            schema = schema.merge_policy(#policy);
        }
    });

    let (impl_generics, ty_generics, where_clause) = generics.split_for_impl();

    Ok(quote! {
        impl #impl_generics ::terrascale::schema::Document for #name #ty_generics #where_clause {
            fn collection() -> &'static str {
                #collection_name
            }

            fn schema() -> ::terrascale::schema::CollectionSchema {
                let mut schema = ::terrascale::schema::CollectionSchema::new(Self::collection());
                #(#field_tokens)*
                #history
                #ttl
                #merge_policy
                schema
            }
        }
    })
}
