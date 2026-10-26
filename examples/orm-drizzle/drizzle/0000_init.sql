CREATE TABLE `drizzle_authors` (
	`id` integer PRIMARY KEY NOT NULL,
	`name` text NOT NULL,
	`email` text NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `drizzle_authors_email_unique` ON `drizzle_authors` (`email`);--> statement-breakpoint
CREATE TABLE `drizzle_posts` (
	`id` integer PRIMARY KEY NOT NULL,
	`author_id` integer NOT NULL,
	`title` text NOT NULL,
	`published` integer DEFAULT false NOT NULL,
	FOREIGN KEY (`author_id`) REFERENCES `drizzle_authors`(`id`) ON UPDATE no action ON DELETE no action
);
--> statement-breakpoint
CREATE INDEX `drizzle_posts_author_id_idx` ON `drizzle_posts` (`author_id`);