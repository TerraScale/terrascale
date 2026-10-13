-- Synthetic profile fixture. No customer data.
SET NAMES utf8mb4;
SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';
SET TIME_ZONE='+00:00';
/*!40101 SET @saved_cs_client=@@character_set_client */;
/*!50503 SET character_set_client=utf8mb4 */;
CREATE TABLE `orders` (
  `id` bigint unsigned NOT NULL,
  `amount` decimal(65,30) NOT NULL,
  `label` varchar(80) NOT NULL DEFAULT '',
  `note` text,
  `raw` binary(4) NOT NULL,
  `optional_raw` blob,
  `day` date NOT NULL,
  `local_time` datetime(6) NOT NULL,
  `instant` timestamp(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  `state` enum('new','paid','a\'b') NOT NULL,
  `tags` set('red','green','blue') NOT NULL,
  `duration` time(6) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `by_label` (`label`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
/*!40101 SET character_set_client=@saved_cs_client */;
INSERT INTO `orders` (`id`,`amount`,`label`,`note`,`raw`,`optional_raw`,`day`,`local_time`,`instant`,`state`,`tags`,`duration`) VALUES
(18446744073709551615,12345678901234567890123456789012345.123456789012345678901234567890,'Olá 😀\tline\nquote\' slash\\','\\N',0x000a5cff,0x,'2024-02-29','2024-02-29 12:34:56.123456','2024-02-29 12:34:56.123456','paid','red,blue','-838:59:59.123456'),
(0,-0.000000000000000000000000000000,'',NULL,0xffffffff,NULL,'1000-01-01','1000-01-01 00:00:00.000000','1970-01-01 00:00:01.000000','a\'b','','00:00:00.000000');
CREATE TABLE `Line "Items"` (
  `order` bigint unsigned NOT NULL,
  `part:id` varchar(32) NOT NULL,
  `a``b` tinyint(1) NOT NULL,
  PRIMARY KEY (`order`,`part:id`),
  UNIQUE KEY `item_unique` (`part:id`),
  CONSTRAINT `item_order` FOREIGN KEY (`order`) REFERENCES `orders` (`id`) ON DELETE CASCADE ON UPDATE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
INSERT INTO `Line "Items"` (`a``b`,`part:id`,`order`) VALUES (1,'x:%',18446744073709551615);
