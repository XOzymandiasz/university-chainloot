
// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC721} from "../lib/openzeppelin-contracts/contracts/token/ERC721/ERC721.sol";

contract GameItems is ERC721 {
    enum Status {
        ACTIVE,
        REDEEMED
    }

    mapping(uint256 => Status) private statuses;

    error ItemAlreadyExists(uint256 tokenId);

    constructor() ERC721("ChainLoot Items", "LOOT") {}

    function issue(
        address recipient,
        uint128 uuid
    ) external returns (uint256) {
        uint256 tokenId = uint256(uuid);

        if (_ownerOf(tokenId) != address(0)) {
            revert ItemAlreadyExists(tokenId);
        }

        _mint(recipient, tokenId);
        statuses[tokenId] = Status.ACTIVE;

        return tokenId;
    }

    function getStatus(
        uint256 tokenId
    ) external view returns (Status) {
        ownerOf(tokenId);
        return statuses[tokenId];
    }
}
