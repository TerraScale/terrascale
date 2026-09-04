import { defineSchema, string, timestamp } from "@terrascale/sdk";

/** The schema source is kept beside terrascale.schema.json for codegen. */
export const schema = defineSchema({
  posts: {
    fields: {
      title: string().required(),
      authorId: string().required().indexed(),
      publishedAt: timestamp().required(),
    },
    indexes: { byAuthor: ["authorId"] },
  },
});

export const posts = schema.toObject().collections.find((collection) => collection.name === "posts")!;
export const title = posts.fields.find((field) => field.name === "title")!;
export const authorId = posts.fields.find((field) => field.name === "authorId")!;
export const publishedAt = posts.fields.find((field) => field.name === "publishedAt")!;
