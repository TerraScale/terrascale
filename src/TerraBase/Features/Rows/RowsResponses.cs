using TerraBase.Features.Sql;

namespace TerraBase.Features.Rows;

internal static class RowsResponses
{
    public static Result<RowAnswer, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> Single(
        Result<IReadOnlyList<RowAnswer>, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> outcome) => outcome switch
        {
            IReadOnlyList<RowAnswer> { Count: > 0 } answers => answers[0],
            IReadOnlyList<RowAnswer> => new NotFound("the row does not exist", "row_not_found"),
            SqlRefusal refusal => refusal,
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
}
